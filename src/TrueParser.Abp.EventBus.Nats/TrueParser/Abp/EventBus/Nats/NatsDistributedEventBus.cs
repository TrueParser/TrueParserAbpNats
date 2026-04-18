using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public class NatsDistributedEventBus : DistributedEventBusBase, ISingletonDependency, IOnApplicationShutdown, IDisposable
{
    protected NatsDistributedEventBusOptions NatsOptions { get; }
    protected IJetStreamContextAccessor JetStreamContextAccessor { get; }
    protected INatsEventSerializer Serializer { get; }
    protected ILogger<NatsDistributedEventBus> Logger { get; set; }

    protected ConcurrentDictionary<Type, List<IEventHandlerFactory>> HandlerFactories { get; }
    protected ConcurrentDictionary<string, List<IEventHandlerFactory>> DynamicHandlerFactories { get; }
    protected ConcurrentDictionary<string, Type> EventTypes { get; }
    protected ConcurrentDictionary<string, CancellationTokenSource> ConsumerCancellationSources { get; }
    protected ConcurrentDictionary<string, TaskCompletionSource> ConsumerStartupSignals { get; }

    private volatile bool _streamCreated;
    private readonly SemaphoreSlim _streamSemaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    public NatsDistributedEventBus(
        IOptions<NatsDistributedEventBusOptions> natsOptions,
        IJetStreamContextAccessor jetStreamContextAccessor,
        INatsEventSerializer serializer,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<AbpDistributedEventBusOptions> distributedEventBusOptions,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IGuidGenerator guidGenerator,
        IClock clock,
        IEventHandlerInvoker eventHandlerInvoker,
        ILocalEventBus localEventBus,
        ICorrelationIdProvider correlationIdProvider,
        ILogger<NatsDistributedEventBus> logger)
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
        JetStreamContextAccessor = jetStreamContextAccessor;
        Serializer = serializer;
        Logger = logger;

        HandlerFactories = new ConcurrentDictionary<Type, List<IEventHandlerFactory>>();
        DynamicHandlerFactories = new ConcurrentDictionary<string, List<IEventHandlerFactory>>();
        EventTypes = new ConcurrentDictionary<string, Type>();
        ConsumerCancellationSources = new ConcurrentDictionary<string, CancellationTokenSource>();
        ConsumerStartupSignals = new ConcurrentDictionary<string, TaskCompletionSource>();
    }

    public virtual async Task InitializeAsync()
    {
        await EnsureStreamExistsAsync();
        SubscribeHandlers(AbpDistributedEventBusOptions.Handlers);
        await WaitForConsumerStartupsAsync();
    }

    protected virtual async Task EnsureStreamExistsAsync()
    {
        if (_streamCreated) return;

        await _streamSemaphore.WaitAsync();
        try
        {
            if (_streamCreated) return;

            var js = await JetStreamContextAccessor.GetContextAsync(NatsOptions.ConnectionName);

            var streamConfig = new StreamConfig(NatsOptions.StreamName, [$"{NatsOptions.SubjectPrefix}.>"])
            {
                Retention = NatsOptions.Retention,
                NumReplicas = NatsOptions.ReplicaCount
            };

            var maxAge = ParseMaxAge(NatsOptions.MaxAge);
            if (maxAge.HasValue)
            {
                streamConfig.MaxAge = maxAge.Value;
            }

            try
            {
                await js.CreateStreamAsync(streamConfig);
                _streamCreated = true;
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058)
            {
                // Stream already exists — that is fine
                _streamCreated = true;
            }
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

        var added = false;
        var shouldSubscribe = false;
        handlerFactories.Locking(factories =>
        {
            if (factory.IsInFactories(factories))
            {
                return;
            }

            shouldSubscribe = factories.Count == 0;
            factories.Add(factory);
            added = true;
        });

        if (!added)
        {
            return NullDisposable.Instance;
        }

        if (shouldSubscribe)
        {
            var eventName = EventNameAttribute.GetNameOrDefault(eventType);
            StartConsumer(eventName);
        }

        return new EventHandlerFactoryUnregistrar(this, eventType, factory);
    }

    // ── Dynamic (string-keyed) Subscribe ─────────────────────────────────────

    public override IDisposable Subscribe(string eventName, IEventHandlerFactory factory)
    {
        var handlerFactories = GetOrCreateDynamicHandlerFactories(eventName);

        var added = false;
        var shouldSubscribe = false;
        handlerFactories.Locking(factories =>
        {
            if (factory.IsInFactories(factories))
            {
                return;
            }

            shouldSubscribe = factories.Count == 0;
            factories.Add(factory);
            added = true;
        });

        if (!added)
        {
            return NullDisposable.Instance;
        }

        if (shouldSubscribe)
        {
            StartConsumer(eventName);
        }

        return new DynamicEventHandlerFactoryUnregistrar(this, eventName, factory);
    }

    // ── NATS consumer ─────────────────────────────────────────────────────────

    protected virtual async Task SubscribeToSubjectAsync(
        string eventName,
        CancellationToken consumerCancellationToken,
        TaskCompletionSource startupSignal)
    {
        try
        {
            var subject = GetSubjectName(eventName);
            var consumerName = SanitizeConsumerName($"{NatsOptions.StreamName}_{eventName}");

            // CreateLinkedTokenSource can throw ObjectDisposedException if _shutdownCts was already disposed.
            // That is caught by the outer catch below, which signals startup complete and exits.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, consumerCancellationToken);
            var shutdownToken = linkedCts.Token;

            while (!shutdownToken.IsCancellationRequested)
            {
                try
                {
                    // Resolve js and ensure stream inside the retry loop so transient
                    // connection failures are retried rather than aborting the consumer.
                    var js = await JetStreamContextAccessor.GetContextAsync(NatsOptions.ConnectionName);
                    await EnsureStreamExistsAsync();

                    INatsJSConsumer consumer;
                    try
                    {
                        // Existing durable consumer — bind without touching config.
                        // The stored delivery sequence overrides DeliverPolicy, so
                        // restarts resume from the last acked position automatically.
                        consumer = await js.GetConsumerAsync(NatsOptions.StreamName, consumerName, shutdownToken);
                    }
                    catch (NatsJSApiException ex) when (ex.Error.Code == 404)
                    {
                        // New consumer (first deployment of this event type).
                        // DeliverPolicy.All ensures messages already retained in the
                        // stream — kept alive by other consumers' Interest — are not
                        // silently skipped. DeliverPolicy.New would miss that backlog.
                        var consumerConfig = new ConsumerConfig(consumerName)
                        {
                            FilterSubject = subject,
                            AckPolicy = ConsumerConfigAckPolicy.Explicit,
                            DeliverPolicy = ConsumerConfigDeliverPolicy.All
                        };

                        var prefetchCount = ParsePrefetchCount(NatsOptions.PrefetchCount);
                        if (prefetchCount.HasValue)
                        {
                            consumerConfig.MaxAckPending = prefetchCount.Value;
                        }

                        consumer = await js.CreateOrUpdateConsumerAsync(NatsOptions.StreamName, consumerConfig, shutdownToken);
                    }

                    startupSignal.TrySetResult();

                    await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: shutdownToken))
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

                    if (!shutdownToken.IsCancellationRequested)
                    {
                        Logger.LogWarning("NATS consumer stream ended for event: {EventName}. Restarting.", eventName);
                    }
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "NATS consumer loop failed for event: {EventName}. Restarting.", eventName);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Signal startup complete even on failure — a consumer error must not prevent
            // the application from starting. The consumer loop above retries on transient
            // errors; this outer catch only fires for unrecoverable setup failures (e.g.
            // _shutdownCts already disposed during a concurrent shutdown).
            startupSignal.TrySetResult();
            Logger.LogError(ex, "Failed to subscribe to NATS subject for event: {EventName}", eventName);
        }
    }

    public virtual void OnApplicationShutdown(ApplicationShutdownContext context)
    {
        try { _shutdownCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public virtual Task OnApplicationShutdownAsync(ApplicationShutdownContext context)
    {
        try { _shutdownCts.Cancel(); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownCts.Cancel();

        foreach (var kvp in ConsumerCancellationSources.ToArray())
        {
            if (ConsumerCancellationSources.TryRemove(kvp.Key, out var consumerCancellationSource))
            {
                consumerCancellationSource.Cancel();
                consumerCancellationSource.Dispose();
            }
        }

        _shutdownCts.Dispose();
        _streamSemaphore.Dispose();
    }

    private static Guid? GetTenantId(NatsJSMsg<byte[]> msg)
    {
        var tenantIdValue = msg.Headers?.TryGetValue("Abp-Tenant-Id", out var values) == true
            ? values.FirstOrDefault()?.ToString()
            : null;

        return Guid.TryParse(tenantIdValue, out var tenantId)
            ? tenantId
            : null;
    }

    private CancellationTokenSource GetOrCreateConsumerCancellationSource(string eventName)
    {
        return ConsumerCancellationSources.GetOrAdd(eventName, _ => new CancellationTokenSource());
    }

    private void StartConsumer(string eventName)
    {
        var consumerCancellationSource = GetOrCreateConsumerCancellationSource(eventName);
        var startupSignal = ConsumerStartupSignals.GetOrAdd(
            eventName,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        _ = Task.Run(() => SubscribeToSubjectAsync(eventName, consumerCancellationSource.Token, startupSignal));
    }

    private async Task WaitForConsumerStartupsAsync()
    {
        var startupTasks = ConsumerStartupSignals.Values.Select(signal => signal.Task).ToArray();
        if (startupTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(startupTasks).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Logger.LogWarning("Timed out waiting for NATS consumers to start after 30 seconds. Continuing application startup.");
        }
    }

    private void StopConsumerIfNoHandlers(string eventName, Type? eventType = null)
    {
        var hasHandlers = eventType != null
            ? HandlerFactories.TryGetValue(eventType, out var typedFactories) && typedFactories.Locking(factories => factories.Any())
            : DynamicHandlerFactories.TryGetValue(eventName, out var dynamicFactories) && dynamicFactories.Locking(factories => factories.Any());

        if (!hasHandlers)
        {
            StopConsumer(eventName);
        }
    }

    private void StopConsumer(string eventName)
    {
        if (ConsumerCancellationSources.TryRemove(eventName, out var consumerCancellationSource))
        {
            consumerCancellationSource.Cancel();
            consumerCancellationSource.Dispose();
        }

        ConsumerStartupSignals.TryRemove(eventName, out _);
    }

    private static TimeSpan? ParseMaxAge(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, out var duration))
        {
            return duration;
        }

        var trimmed = value.Trim();
        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];

        if (!double.TryParse(numberPart, out var amount))
        {
            return null;
        }

        return unit switch
        {
            's' or 'S' => TimeSpan.FromSeconds(amount),
            'm' or 'M' => TimeSpan.FromMinutes(amount),
            'h' or 'H' => TimeSpan.FromHours(amount),
            'd' or 'D' => TimeSpan.FromDays(amount),
            _ => null
        };
    }

    private static long? ParsePrefetchCount(string? value)
    {
        if (!long.TryParse(value, out var prefetchCount) || prefetchCount <= 0)
        {
            return null;
        }

        return prefetchCount;
    }

    private async Task ProcessMessageAsync(string eventName, NatsJSMsg<byte[]> msg)
    {
        if (msg.Data == null) return;

        var correlationId = msg.Headers?.TryGetValue("Abp-Correlation-Id", out var values) == true
            ? values.FirstOrDefault()?.ToString()
            : null;
        var tenantId = GetTenantId(msg);

        using (CurrentTenant.Change(tenantId))
        {
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
            else
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
                    var dynamicFactories = DynamicHandlerFactories
                        .Where(hf => MatchesEventName(hf.Key, eventName))
                        .SelectMany(hf => hf.Value.Locking(factories => factories.ToList()))
                        .ToList();

                    foreach (var factory in dynamicFactories)
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

        await TriggerDistributedEventSentAsync(new DistributedEventSent
        {
            Source = DistributedEventSource.Direct,
            EventName = eventName,
            EventData = eventData
        });
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
        await EnsureStreamExistsAsync();
        var js = await JetStreamContextAccessor.GetContextAsync(NatsOptions.ConnectionName);

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
        var eventType = typeof(TEvent);
        var eventName = EventNameAttribute.GetNameOrDefault(eventType);

        GetOrCreateHandlerFactories(eventType)
            .Locking(factories => factories.RemoveAll(factory =>
            {
                if (factory is SingleInstanceHandlerFactory singleFactory &&
                    singleFactory.HandlerInstance is ActionEventHandler<TEvent> actionHandler)
                {
                    return actionHandler.Action == action;
                }
                return false;
            }));

        StopConsumerIfNoHandlers(eventName, eventType);
    }

    public override void Unsubscribe(Type eventType, IEventHandler handler)
    {
        var eventName = EventNameAttribute.GetNameOrDefault(eventType);
        GetOrCreateHandlerFactories(eventType)
            .Locking(factories => factories.RemoveAll(factory =>
                factory is SingleInstanceHandlerFactory singleFactory &&
                singleFactory.HandlerInstance == handler));

        StopConsumerIfNoHandlers(eventName, eventType);
    }

    public override void Unsubscribe(Type eventType, IEventHandlerFactory factory)
    {
        var eventName = EventNameAttribute.GetNameOrDefault(eventType);
        GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Remove(factory));
        StopConsumerIfNoHandlers(eventName, eventType);
    }

    public override void UnsubscribeAll(Type eventType)
    {
        var eventName = EventNameAttribute.GetNameOrDefault(eventType);
        GetOrCreateHandlerFactories(eventType).Locking(factories => factories.Clear());
        StopConsumer(eventName);
    }

    public override void Unsubscribe(string eventName, IEventHandlerFactory factory)
    {
        GetOrCreateDynamicHandlerFactories(eventName).Locking(factories => factories.Remove(factory));
        StopConsumerIfNoHandlers(eventName);
    }

    public override void Unsubscribe(string eventName, IEventHandler handler)
    {
        GetOrCreateDynamicHandlerFactories(eventName)
            .Locking(factories => factories.RemoveAll(factory =>
                factory is SingleInstanceHandlerFactory singleFactory &&
                singleFactory.HandlerInstance == handler));

        StopConsumerIfNoHandlers(eventName);
    }

    public override void UnsubscribeAll(string eventName)
    {
        GetOrCreateDynamicHandlerFactories(eventName).Locking(factories => factories.Clear());
        StopConsumer(eventName);
    }

    // ── Handler factory lookup ────────────────────────────────────────────────

    protected override IEnumerable<EventTypeWithEventHandlerFactories> GetHandlerFactories(Type eventType)
    {
        var result = new List<EventTypeWithEventHandlerFactories>();
        var eventNames = EventTypes.Where(x => ShouldTriggerEventForHandler(eventType, x.Value)).Select(x => x.Key).ToList();

        foreach (var hf in HandlerFactories.Where(hf => ShouldTriggerEventForHandler(eventType, hf.Key)))
        {
            result.Add(new EventTypeWithEventHandlerFactories(hf.Key, hf.Value.Locking(factories => factories.ToList())));
        }

        foreach (var hf in DynamicHandlerFactories.Where(hf => eventNames.Contains(hf.Key)))
        {
            result.Add(new EventTypeWithEventHandlerFactories(typeof(DynamicEventData), hf.Value.Locking(factories => factories.ToList())));
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
        foreach (var hf in DynamicHandlerFactories.Where(hf => MatchesEventName(hf.Key, eventName)))
        {
            result.Add(new EventTypeWithEventHandlerFactories(typeof(DynamicEventData), hf.Value.Locking(factories => factories.ToList())));
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

    private static bool MatchesEventName(string pattern, string eventName)
    {
        var patternParts = pattern.Split('.');
        var eventParts = eventName.Split('.');

        var eventIndex = 0;

        for (var patternIndex = 0; patternIndex < patternParts.Length; patternIndex++)
        {
            var patternPart = patternParts[patternIndex];

            if (patternPart == ">")
            {
                return patternIndex == patternParts.Length - 1;
            }

            if (eventIndex >= eventParts.Length)
            {
                return false;
            }

            if (patternPart != "*" && !string.Equals(patternPart, eventParts[eventIndex], StringComparison.Ordinal))
            {
                return false;
            }

            eventIndex++;
        }

        return eventIndex == eventParts.Length;
    }
}
