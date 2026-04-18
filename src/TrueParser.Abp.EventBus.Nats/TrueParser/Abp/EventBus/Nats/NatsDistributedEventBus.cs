using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using TrueParser.Abp.Nats;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Timing;
using Volo.Abp.Tracing;
using Volo.Abp.Uow;

namespace TrueParser.Abp.EventBus.Nats;

[Dependency(ReplaceServices = true)]
[ExposeServices(typeof(IDistributedEventBus), typeof(NatsDistributedEventBus))]
public class NatsDistributedEventBus : DistributedEventBusBase, ISingletonDependency
{
    protected NatsDistributedEventBusOptions NatsOptions { get; }
    protected INatsConnectionPool ConnectionPool { get; }
    protected INatsEventSerializer Serializer { get; }
    protected ILogger<NatsDistributedEventBus> Logger { get; set; }

    protected ConcurrentDictionary<Type, List<IEventHandlerFactory>> HandlerFactories { get; }
    protected ConcurrentDictionary<string, List<IEventHandlerFactory>> DynamicHandlerFactories { get; }
    protected ConcurrentDictionary<string, Type> EventTypes { get; }

    private bool _streamCreated;
    private readonly SemaphoreSlim _streamSemaphore = new(1, 1);

    public NatsDistributedEventBus(
        IOptions<NatsDistributedEventBusOptions> natsOptions,
        INatsConnectionPool connectionPool,
        INatsEventSerializer serializer,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<AbpDistributedEventBusOptions> distributedEventBusOptions,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IGuidGenerator guidGenerator,
        IClock clock,
        IEventHandlerInvoker eventHandlerInvoker,
        ILocalEventBus localEventBus,
        ICorrelationIdProvider correlationIdProvider)
        : base(
            serviceScopeFactory,
            currentTenant,
            unitOfWorkManager,
            distributedEventBusOptions,
            guidGenerator,
            clock,
            eventHandlerInvoker,
            localEventBus,
            correlationIdProvider)
    {
        NatsOptions = natsOptions.Value;
        ConnectionPool = connectionPool;
        Serializer = serializer;
        Logger = NullLogger<NatsDistributedEventBus>.Instance;

        HandlerFactories = new ConcurrentDictionary<Type, List<IEventHandlerFactory>>();
        DynamicHandlerFactories = new ConcurrentDictionary<string, List<IEventHandlerFactory>>();
        EventTypes = new ConcurrentDictionary<string, Type>();
    }

    public virtual async Task InitializeAsync()
    {
        await EnsureStreamExistsAsync();
        SubscribeHandlers(AbpDistributedEventBusOptions.Handlers);
    }

    protected virtual async Task EnsureStreamExistsAsync()
    {
        if (_streamCreated) return;

        await _streamSemaphore.WaitAsync();
        try
        {
            if (_streamCreated) return;

            var connection = await ConnectionPool.GetAsync(NatsOptions.ConnectionName);
            var js = connection.CreateJetStreamContext();

            var streamConfig = new StreamConfig(NatsOptions.StreamName, [$"{NatsOptions.SubjectPrefix}.>"])
            {
                Retention = NatsOptions.Retention,
                NumReplicas = NatsOptions.ReplicaCount
            };

            try
            {
                await js.CreateStreamAsync(streamConfig);
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058)
            {
                // Stream already exists — that is fine
            }
            _streamCreated = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not ensure NATS JetStream stream exists.");
            throw;
        }
        finally
        {
            _streamSemaphore.Release();
        }
    }

    // ── Typed Subscribe ───────────────────────────────────────────────────────

    public override IDisposable Subscribe(Type eventType, IEventHandlerFactory factory)
    {
        var handlerFactories = GetOrCreateHandlerFactories(eventType);

        if (factory.IsInFactories(handlerFactories))
        {
            return NullDisposable.Instance;
        }

        handlerFactories.Add(factory);

        if (handlerFactories.Count == 1)
        {
            _ = Task.Run(() => SubscribeToSubjectAsync(EventNameAttribute.GetNameOrDefault(eventType)));
        }

        return new EventHandlerFactoryUnregistrar(this, eventType, factory);
    }

    // ── Dynamic (string-keyed) Subscribe ─────────────────────────────────────

    public override IDisposable Subscribe(string eventName, IEventHandlerFactory factory)
    {
        var handlerFactories = GetOrCreateDynamicHandlerFactories(eventName);

        if (factory.IsInFactories(handlerFactories))
        {
            return NullDisposable.Instance;
        }

        handlerFactories.Add(factory);

        if (handlerFactories.Count == 1)
        {
            _ = Task.Run(() => SubscribeToSubjectAsync(eventName));
        }

        return new DynamicEventHandlerFactoryUnregistrar(this, eventName, factory);
    }

    // ── NATS consumer ─────────────────────────────────────────────────────────

    protected virtual async Task SubscribeToSubjectAsync(string eventName)
    {
        try
        {
            var subject = GetSubjectName(eventName);
            var consumerName = SanitizeConsumerName($"{NatsOptions.StreamName}_{eventName}");

            var connection = await ConnectionPool.GetAsync(NatsOptions.ConnectionName);
            var js = connection.CreateJetStreamContext();

            await EnsureStreamExistsAsync();

            var consumer = await js.CreateOrUpdateConsumerAsync(NatsOptions.StreamName, new ConsumerConfig(consumerName)
            {
                FilterSubject = subject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All
            });

            _ = Task.Run(async () =>
            {
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: CancellationToken.None))
                {
                    try
                    {
                        await ProcessMessageAsync(eventName, msg);
                        await msg.AckAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error processing NATS message for event: {EventName}", eventName);
                        await msg.NakAsync();
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to subscribe to NATS subject for event: {EventName}", eventName);
        }
    }

    private async Task ProcessMessageAsync(string eventName, NatsJSMsg<byte[]> msg)
    {
        if (msg.Data == null) return;

        var correlationId = msg.Headers?.TryGetValue("Abp-Correlation-Id", out var values) == true
            ? values.FirstOrDefault()?.ToString()
            : null;

        var eventType = EventTypes.GetOrDefault(eventName);

        if (eventType != null)
        {
            var eventData = Serializer.Deserialize(msg.Data, eventType);
            if (await AddToInboxAsync(null, eventName, eventType, eventData, correlationId))
            {
                return;
            }
            using (CorrelationIdProvider.Change(correlationId))
            {
                await TriggerHandlersDirectAsync(eventType, eventData);
            }
        }
        else if (DynamicHandlerFactories.TryGetValue(eventName, out var dynamicFactories))
        {
            var rawData = Serializer.Deserialize<object>(msg.Data);
            var dynamicEventData = new DynamicEventData(eventName, rawData);

            if (await AddToInboxAsync(null, eventName, typeof(DynamicEventData), dynamicEventData, correlationId))
            {
                return;
            }

            using (CorrelationIdProvider.Change(correlationId))
            {
                await TriggerDistributedEventReceivedAsync(new DistributedEventReceived
                {
                    Source = DistributedEventSource.Direct,
                    EventName = eventName,
                    EventData = dynamicEventData
                });

                var exceptions = new List<Exception>();
                foreach (var factory in dynamicFactories.ToList())
                {
                    await TriggerHandlerAsync(factory, typeof(DynamicEventData), dynamicEventData, exceptions);
                }

                if (exceptions.Count > 0)
                {
                    ThrowOriginalExceptions(typeof(DynamicEventData), exceptions);
                }
            }
        }
    }

    // ── Publish ───────────────────────────────────────────────────────────────

    protected override async Task PublishToEventBusAsync(Type eventType, object eventData)
    {
        string eventName;
        byte[] body;

        if (eventType == typeof(DynamicEventData) && eventData is DynamicEventData dynamicEvent)
        {
            eventName = dynamicEvent.EventName;
            body = Serializer.Serialize(dynamicEvent.Data);
        }
        else
        {
            eventName = EventNameAttribute.GetNameOrDefault(eventType);
            body = Serializer.Serialize(eventData);
        }

        await PublishToNatsAsync(eventName, body);
    }

    public override Task PublishAsync(string eventName, object eventData, bool onUnitOfWorkComplete = true)
    {
        var eventType = EventTypes.GetOrDefault(eventName);
        var dynamicEventData = eventData as DynamicEventData ?? new DynamicEventData(eventName, eventData);

        if (eventType != null)
        {
            return PublishAsync(eventType, ConvertDynamicEventData(dynamicEventData.Data, eventType), onUnitOfWorkComplete);
        }

        return PublishAsync(typeof(DynamicEventData), dynamicEventData, onUnitOfWorkComplete);
    }

    private async Task PublishToNatsAsync(string eventName, byte[] body, string? correlationId = null)
    {
        var subject = GetSubjectName(eventName);
        var connection = await ConnectionPool.GetAsync(NatsOptions.ConnectionName);
        var js = connection.CreateJetStreamContext();

        var headers = new NATS.Client.Core.NatsHeaders();
        correlationId ??= CorrelationIdProvider.Get();
        if (!string.IsNullOrEmpty(correlationId))
        {
            headers.Add("Abp-Correlation-Id", correlationId);
        }

        if (CurrentTenant.Id.HasValue)
        {
            headers.Add("Abp-Tenant-Id", CurrentTenant.Id.Value.ToString());
        }

        await js.PublishAsync(subject, body, headers: headers);
    }

    // ── Outbox / Inbox ────────────────────────────────────────────────────────

    public override async Task PublishFromOutboxAsync(OutgoingEventInfo outgoingEvent, OutboxConfig outboxConfig)
    {
        await PublishToNatsAsync(outgoingEvent.EventName, outgoingEvent.EventData, outgoingEvent.GetCorrelationId());

        using (CorrelationIdProvider.Change(outgoingEvent.GetCorrelationId()))
        {
            await TriggerDistributedEventSentAsync(new DistributedEventSent
            {
                Source = DistributedEventSource.Outbox,
                EventName = outgoingEvent.EventName,
                EventData = outgoingEvent.EventData
            });
        }
    }

    public override async Task PublishManyFromOutboxAsync(IEnumerable<OutgoingEventInfo> outgoingEvents, OutboxConfig outboxConfig)
    {
        foreach (var outgoingEvent in outgoingEvents)
        {
            await PublishFromOutboxAsync(outgoingEvent, outboxConfig);
        }
    }

    public override async Task ProcessFromInboxAsync(IncomingEventInfo incomingEvent, InboxConfig inboxConfig)
    {
        var eventType = EventTypes.GetOrDefault(incomingEvent.EventName);
        if (eventType == null) return;

        var eventData = Serializer.Deserialize(incomingEvent.EventData, eventType);
        var exceptions = new List<Exception>();

        using (CorrelationIdProvider.Change(incomingEvent.GetCorrelationId()))
        {
            await TriggerHandlersFromInboxAsync(eventType, eventData, exceptions, inboxConfig);
        }

        if (exceptions.Any())
        {
            ThrowOriginalExceptions(eventType, exceptions);
        }
    }

    // ── Unsubscribe ───────────────────────────────────────────────────────────

    public override void Unsubscribe<TEvent>(Func<TEvent, Task> action)
    {
        Check.NotNull(action, nameof(action));
        GetOrCreateHandlerFactories(typeof(TEvent))
            .Locking(factories => factories.RemoveAll(factory =>
            {
                if (factory is SingleInstanceHandlerFactory singleFactory &&
                    singleFactory.HandlerInstance is ActionEventHandler<TEvent> actionHandler)
                {
                    return actionHandler.Action == action;
                }
                return false;
            }));
    }

    public override void Unsubscribe(Type eventType, IEventHandler handler)
    {
        GetOrCreateHandlerFactories(eventType)
            .Locking(factories => factories.RemoveAll(factory =>
                factory is SingleInstanceHandlerFactory singleFactory &&
                singleFactory.HandlerInstance == handler));
    }

    public override void Unsubscribe(Type eventType, IEventHandlerFactory factory)
    {
        GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Remove(factory));
    }

    public override void UnsubscribeAll(Type eventType)
    {
        GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Clear());
    }

    public override void Unsubscribe(string eventName, IEventHandlerFactory factory)
    {
        GetOrCreateDynamicHandlerFactories(eventName).Locking(factories => factories.Remove(factory));
    }

    public override void Unsubscribe(string eventName, IEventHandler handler)
    {
        GetOrCreateDynamicHandlerFactories(eventName)
            .Locking(factories => factories.RemoveAll(factory =>
                factory is SingleInstanceHandlerFactory singleFactory &&
                singleFactory.HandlerInstance == handler));
    }

    public override void UnsubscribeAll(string eventName)
    {
        GetOrCreateDynamicHandlerFactories(eventName).Locking(factories => factories.Clear());
    }

    // ── Handler factory lookup ────────────────────────────────────────────────

    protected override IEnumerable<EventTypeWithEventHandlerFactories> GetHandlerFactories(Type eventType)
    {
        var result = new List<EventTypeWithEventHandlerFactories>();
        var eventNames = EventTypes.Where(x => ShouldTriggerEventForHandler(eventType, x.Value)).Select(x => x.Key).ToList();

        foreach (var hf in HandlerFactories.Where(hf => ShouldTriggerEventForHandler(eventType, hf.Key)))
        {
            result.Add(new EventTypeWithEventHandlerFactories(hf.Key, hf.Value));
        }

        foreach (var hf in DynamicHandlerFactories.Where(hf => eventNames.Contains(hf.Key)))
        {
            result.Add(new EventTypeWithEventHandlerFactories(typeof(DynamicEventData), hf.Value));
        }

        return result.ToArray();
    }

    protected override IEnumerable<EventTypeWithEventHandlerFactories> GetDynamicHandlerFactories(string eventName)
    {
        var eventType = GetEventTypeByEventName(eventName);
        if (eventType != null)
        {
            return GetHandlerFactories(eventType);
        }

        var result = new List<EventTypeWithEventHandlerFactories>();
        foreach (var hf in DynamicHandlerFactories.Where(hf => hf.Key == eventName))
        {
            result.Add(new EventTypeWithEventHandlerFactories(typeof(DynamicEventData), hf.Value));
        }

        return result;
    }

    protected override Type? GetEventTypeByEventName(string eventName)
    {
        return EventTypes.GetOrDefault(eventName);
    }

    // ── Serialization ─────────────────────────────────────────────────────────

    protected override byte[] Serialize(object eventData)
    {
        return Serializer.Serialize(eventData);
    }

    // ── UoW ──────────────────────────────────────────────────────────────────

    protected override void AddToUnitOfWork(IUnitOfWork unitOfWork, UnitOfWorkEventRecord eventRecord)
    {
        unitOfWork.AddOrReplaceDistributedEvent(eventRecord);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    protected virtual string GetSubjectName(string eventName)
    {
        return $"{NatsOptions.SubjectPrefix}.{eventName}";
    }

    private static string SanitizeConsumerName(string name)
    {
        return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9\-_]", "_");
    }

    private List<IEventHandlerFactory> GetOrCreateHandlerFactories(Type eventType)
    {
        return HandlerFactories.GetOrAdd(eventType, type =>
        {
            var eventName = EventNameAttribute.GetNameOrDefault(type);
            EventTypes.GetOrAdd(eventName, eventType);
            return new List<IEventHandlerFactory>();
        });
    }

    private List<IEventHandlerFactory> GetOrCreateDynamicHandlerFactories(string eventName)
    {
        return DynamicHandlerFactories.GetOrAdd(eventName, _ => new List<IEventHandlerFactory>());
    }

    private static bool ShouldTriggerEventForHandler(Type targetEventType, Type handlerEventType)
    {
        return handlerEventType == targetEventType || handlerEventType.IsAssignableFrom(targetEventType);
    }
}
