using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TrueParser.Abp.Nats;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Tracing;
using Volo.Abp.Uow;
using Xunit;
using Shouldly;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsEventBus_Integration_Tests : NatsEventBusTestBase
{
    private readonly IDistributedEventBus _distributedEventBus;

    public NatsEventBus_Integration_Tests()
    {
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
    }

    [NatsFact]
    public async Task Should_Publish_And_Consume_Event()
    {
        // Arrange
        var received = new TaskCompletionSource<TestEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testData = new TestEventData { Message = "Hello NATS!" };

        // We use a local action handler for testing
        using (var serviceScope = ServiceProvider.CreateScope())
        using (_distributedEventBus.Subscribe<TestEventData>(async data =>
        {
            if (data.Message == "Hello NATS!")
            {
                received.TrySetResult(data);
            }

            await Task.CompletedTask;
        }))
        {
            // Act
            var iterations = 0;
            while (!received.Task.IsCompleted && iterations < 20)
            {
                await _distributedEventBus.PublishAsync(testData, onUnitOfWorkComplete: false, useOutbox: false);
                await Task.Delay(100);
                iterations++;
            }

            // Assert
            (await received.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message.ShouldBe("Hello NATS!");
        }
    }

    [NatsFact]
    public async Task Should_Subscribe_With_Wildcard()
    {
        // Arrange
        var receivedValues = new ConcurrentDictionary<int, byte>();
        var receivedEventNames = new ConcurrentDictionary<string, byte>();
        var receivedAllValues = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventPrefix = $"WildcardResolution.{Guid.NewGuid():N}";
        var wildcardEventName = $"{eventPrefix}.*";
        
        // Subject prefix is TrueParser.Test.Events
        // This subscription should catch any events starting with TrueParser.Test.Events.Wildcard
        using var subscription = _distributedEventBus.Subscribe(wildcardEventName, new WildcardTestHandler(eventData =>
        {
            receivedEventNames.TryAdd(eventData.EventName, 0);
            var value = eventData.Data switch
            {
                JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.Object
                    && (jsonElement.TryGetProperty("Value", out var valueProperty)
                        || jsonElement.TryGetProperty("value", out valueProperty))
                    && valueProperty.ValueKind == JsonValueKind.Number
                    => valueProperty.GetInt32(),
                _ => 0
            };
            receivedValues.TryAdd(value, 0);

            if (receivedValues.Count >= 2)
            {
                receivedAllValues.TrySetResult();
            }
        }));

        // Act
        var iterations = 0;
        while (!receivedAllValues.Task.IsCompleted && iterations < 20)
        {
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData($"{eventPrefix}.First", new { Value = 1 }), onUnitOfWorkComplete: false, useOutbox: false);
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData($"{eventPrefix}.Second", new { Value = 2 }), onUnitOfWorkComplete: false, useOutbox: false);
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData("NotWildcard.Something", new { Value = 3 }), onUnitOfWorkComplete: false, useOutbox: false);
            await Task.Delay(100);
            iterations++;
        }

        // Assert
        await receivedAllValues.Task.WaitAsync(TimeSpan.FromSeconds(10));

        receivedValues.ContainsKey(1).ShouldBeTrue();
        receivedValues.ContainsKey(2).ShouldBeTrue();
        receivedValues.ContainsKey(3).ShouldBeFalse();
        receivedEventNames.Count.ShouldBe(2);
        receivedEventNames.ContainsKey($"{eventPrefix}.First").ShouldBeTrue();
        receivedEventNames.ContainsKey($"{eventPrefix}.Second").ShouldBeTrue();
    }

    [NatsFact]
    public async Task Dynamic_Event_Should_Be_Processed_Through_Abp_Inbox()
    {
        var eventName = $"DynamicInbox.Exact.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<DynamicEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializer = GetRequiredService<INatsEventSerializer>();

        using var eventBus = CreateCapturingEventBus();
        using var subscription = eventBus.Subscribe(
            eventName,
            new RetainedEventHandler(data => received.TrySetResult(data)));

        await eventBus.ProcessFromInboxForTestAsync(
            CreateIncomingEvent(eventName, serializer.Serialize(new { Value = 42 })),
            new InboxConfig($"DynamicInbox_{Guid.NewGuid():N}"));

        var eventData = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        eventData.EventName.ShouldBe(eventName);
        eventData.Data.ShouldNotBeNull();
    }

    [NatsFact]
    public async Task Wildcard_Dynamic_Event_Should_Be_Processed_Through_Abp_Inbox_With_Actual_Event_Name()
    {
        var eventPrefix = $"DynamicInbox.Wildcard.{Guid.NewGuid():N}";
        var eventName = $"{eventPrefix}.Created";
        var received = new TaskCompletionSource<DynamicEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializer = GetRequiredService<INatsEventSerializer>();

        using var eventBus = CreateCapturingEventBus();
        using var subscription = eventBus.Subscribe(
            $"{eventPrefix}.*",
            new RetainedEventHandler(data => received.TrySetResult(data)));

        await eventBus.ProcessFromInboxForTestAsync(
            CreateIncomingEvent(eventName, serializer.Serialize(new { Value = 7 })),
            new InboxConfig($"DynamicInbox_{Guid.NewGuid():N}"));

        var eventData = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        eventData.EventName.ShouldBe(eventName);
        eventData.Data.ShouldNotBeNull();
    }

    [NatsFact]
    public async Task Typed_Event_Should_Still_Be_Processed_Through_Abp_Inbox()
    {
        var received = new TaskCompletionSource<TestEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serializer = GetRequiredService<INatsEventSerializer>();

        using var eventBus = CreateCapturingEventBus();
        using var subscription = eventBus.Subscribe(
            new TestEventHandler(data => received.TrySetResult(data)));

        await eventBus.ProcessFromInboxForTestAsync(
            CreateIncomingEvent(
                "TestEvent",
                serializer.Serialize(new TestEventData { Message = "from-inbox" })),
            new InboxConfig($"TypedInbox_{Guid.NewGuid():N}"));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message.ShouldBe("from-inbox");
    }

    [NatsFact]
    public async Task Unknown_Event_Should_Not_Invoke_Unrelated_Dynamic_Handler_From_Abp_Inbox()
    {
        var unrelatedEventName = $"DynamicInbox.Unrelated.{Guid.NewGuid():N}";
        var unknownEventName = $"DynamicInbox.Unknown.{Guid.NewGuid():N}";
        var invocationCount = 0;
        var serializer = GetRequiredService<INatsEventSerializer>();

        using var eventBus = CreateCapturingEventBus();
        using var subscription = eventBus.Subscribe(
            unrelatedEventName,
            new RetainedEventHandler(_ => Interlocked.Increment(ref invocationCount)));

        await eventBus.ProcessFromInboxForTestAsync(
            CreateIncomingEvent(unknownEventName, serializer.Serialize(new { Value = 11 })),
            new InboxConfig($"UnknownInbox_{Guid.NewGuid():N}"));

        invocationCount.ShouldBe(0);
    }

    private CapturingNatsDistributedEventBus CreateCapturingEventBus()
    {
        var options = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value;
        return ActivatorUtilities.CreateInstance<CapturingNatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = $"DynamicInbox_{Guid.NewGuid():N}",
                SubjectPrefix = $"{Guid.NewGuid():N}.TrueParser.DynamicInbox.Events",
                ClientName = $"DynamicInbox_{Guid.NewGuid():N}",
                ConnectionName = options.ConnectionName
            }));
    }

    private static IncomingEventInfo CreateIncomingEvent(string eventName, byte[] eventData)
    {
        return new IncomingEventInfo(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            eventName,
            eventData,
            DateTime.UtcNow);
    }

    [NatsFact]
    public async Task New_Consumer_Should_Receive_Messages_Retained_By_Other_Consumers_Interest()
    {
        // Scenario 2 from R5 analysis:
        // A new consumer type joins a stream where other consumers already exist and have
        // kept messages alive via Interest retention.
        // With DeliverPolicy.New the new consumer silently skips that backlog.
        // With DeliverPolicy.All it catches up on retained messages.
        //
        // Interest retention keeps a message only while a consumer whose FilterSubject
        // MATCHES the message subject exists and hasn't acked it. So we create an "anchor"
        // consumer via the raw NATS API with the exact FilterSubject of the target event.
        // We never start ConsumeAsync on it — it pins the message without consuming it.

        var natsOpts = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value;
        var jsAccessor = GetRequiredService<IJetStreamContextAccessor>();
        var js = await jsAccessor.GetContextAsync(natsOpts.ConnectionName);

        var eventName = $"RetainedEvent.{Guid.NewGuid():N}";
        var subject = $"{natsOpts.SubjectPrefix}.{eventName}";
        var anchorConsumerName = System.Text.RegularExpressions.Regex.Replace(
            $"{natsOpts.StreamName}_Anchor_{Guid.NewGuid():N}",
            @"[^a-zA-Z0-9\-_]", "_");

        // Step 1: Create the anchor consumer in NATS with the same FilterSubject as the
        // target event. This establishes Interest for that subject without consuming.
        var anchorConfig = new ConsumerConfig(anchorConsumerName)
        {
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All
        };
        await js.CreateOrUpdateConsumerAsync(natsOpts.StreamName, anchorConfig);

        try
        {
            // Step 2: Publish the target event. Interest retention keeps it because the
            // anchor consumer exists with a matching FilterSubject and hasn't acked it.
            await _distributedEventBus.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Value = 99 }),
                onUnitOfWorkComplete: false,
                useOutbox: false);

            // Step 3: Subscribe via the event bus — this creates a brand-new durable
            // consumer. With DeliverPolicy.All it receives the message published in step 2.
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = _distributedEventBus.Subscribe(eventName, new RetainedEventHandler(_ =>
            {
                received.TrySetResult();
            }));

            // Step 4: The retained message must arrive within the timeout.
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // Clean up the anchor consumer so it doesn't hold unacked messages in the stream.
            await js.DeleteConsumerAsync(natsOpts.StreamName, anchorConsumerName);
        }
    }

    [NatsFact]
    public async Task Consumer_Should_Stop_And_Start_Again_After_Unsubscribe()
    {
        var eventName = $"Lifecycle.{Guid.NewGuid():N}";
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstSubscription = _distributedEventBus.Subscribe(
            eventName,
            new RetainedEventHandler(_ => firstReceived.TrySetResult()));

        try
        {
            var iterations = 0;
            while (!firstReceived.Task.IsCompleted && iterations++ < 20)
            {
                await _distributedEventBus.PublishAsync(
                    typeof(DynamicEventData),
                    new DynamicEventData(eventName, new { Value = 1 }),
                    onUnitOfWorkComplete: false,
                    useOutbox: false);
                await Task.Delay(100);
            }

            await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            firstSubscription.Dispose();
        }

        await Task.Delay(250);

        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondSubscription = _distributedEventBus.Subscribe(
            eventName,
            new RetainedEventHandler(_ => secondReceived.TrySetResult()));

        var restartIterations = 0;
        while (!secondReceived.Task.IsCompleted && restartIterations++ < 20)
        {
            await _distributedEventBus.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Value = 2 }),
                onUnitOfWorkComplete: false,
                useOutbox: false);
            await Task.Delay(100);
        }

        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [NatsFact]
    public async Task Direct_Publish_Should_Include_A_Stable_Nats_Message_Id()
    {
        var natsOptions = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value;
        var js = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync(natsOptions.ConnectionName);
        var eventName = $"MessageIdentity.{Guid.NewGuid():N}";
        var subject = $"{natsOptions.SubjectPrefix}.{eventName}";
        var consumerName = $"MessageIdentity_{Guid.NewGuid():N}";

        try
        {
            await js.CreateStreamAsync(new StreamConfig(natsOptions.StreamName, [$"{natsOptions.SubjectPrefix}.>"])
            {
                Retention = natsOptions.Retention,
                NumReplicas = natsOptions.ReplicaCount
            });
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058)
        {
            // The test module normally creates the shared stream during startup.
        }

        var consumerConfig = new ConsumerConfig(consumerName)
        {
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New
        };
        var consumer = await js.CreateOrUpdateConsumerAsync(natsOptions.StreamName, consumerConfig);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var messageTask = Task.Run(async () =>
            {
                await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellation.Token))
                {
                    await message.AckAsync();
                    return message;
                }

                return null;
            });

            await _distributedEventBus.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Value = 1 }),
                onUnitOfWorkComplete: false,
                useOutbox: false);

            var message = await messageTask;
            var messageId = message?.Headers?.TryGetValue("Nats-Msg-Id", out var values) == true
                ? values.FirstOrDefault()?.ToString()
                : null;

            messageId.ShouldNotBeNullOrWhiteSpace();
            Guid.TryParse(messageId, out _).ShouldBeTrue();
        }
        finally
        {
            await js.DeleteConsumerAsync(natsOptions.StreamName, consumerName);
        }
    }

    [NatsFact]
    public async Task Outbox_Publish_Should_Use_Outgoing_Event_Id_As_Nats_Message_Id()
    {
        var natsOptions = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value;
        var js = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync(natsOptions.ConnectionName);
        var eventName = $"MessageIdentity.Outbox.{Guid.NewGuid():N}";
        var subject = $"{natsOptions.SubjectPrefix}.{eventName}";
        var consumerName = $"MessageIdentityOutbox_{Guid.NewGuid():N}";
        try
        {
            await js.CreateStreamAsync(new StreamConfig(natsOptions.StreamName, [$"{natsOptions.SubjectPrefix}.>"])
            {
                Retention = natsOptions.Retention,
                NumReplicas = natsOptions.ReplicaCount
            });
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == 10058)
        {
            // The test module normally creates the shared stream during startup.
        }

        var consumerConfig = new ConsumerConfig(consumerName)
        {
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New
        };
        var consumer = await js.CreateOrUpdateConsumerAsync(natsOptions.StreamName, consumerConfig);
        var outgoingId = Guid.NewGuid();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var messageTask = Task.Run(async () =>
            {
                await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellation.Token))
                {
                    await message.AckAsync();
                    return message;
                }

                return null;
            });

            var outgoingEvent = new OutgoingEventInfo(
                outgoingId,
                eventName,
                JsonSerializer.SerializeToUtf8Bytes(new { Value = 2 }),
                DateTime.UtcNow);
            await GetRequiredService<NatsDistributedEventBus>()
                .PublishFromOutboxAsync(outgoingEvent, new OutboxConfig("MessageIdentity"));

            var message = await messageTask;
            var messageId = message?.Headers?.TryGetValue("Nats-Msg-Id", out var values) == true
                ? values.FirstOrDefault()?.ToString()
                : null;

            messageId.ShouldBe(outgoingId.ToString());
        }
        finally
        {
            await js.DeleteConsumerAsync(natsOptions.StreamName, consumerName);
        }
    }

    [NatsFact]
    public async Task Consumed_Message_Id_Should_Be_Passed_To_Abp_Inbox()
    {
        var natsOptions = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value;
        var eventName = $"MessageIdentity.Inbox.{Guid.NewGuid():N}";
        using var eventBus = ActivatorUtilities.CreateInstance<CapturingNatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = natsOptions.StreamName,
                SubjectPrefix = natsOptions.SubjectPrefix,
                ClientName = $"InboxCapture_{Guid.NewGuid():N}"
            }));

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = eventBus.Subscribe(
            eventName,
            new RetainedEventHandler(_ => received.TrySetResult()));

        await eventBus.InitializeAsync();
        const string correlationId = "message-identity-correlation";
        using (GetRequiredService<ICorrelationIdProvider>().Change(correlationId))
        {
            await _distributedEventBus.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Value = 3 }),
                onUnitOfWorkComplete: false,
                useOutbox: false);
        }

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        eventBus.LastMessageId.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParse(eventBus.LastMessageId, out _).ShouldBeTrue();
        eventBus.LastCorrelationId.ShouldBe(correlationId);
    }

    [NatsFact]
    public async Task Abp_Inbox_Should_Deduplicate_Repeated_Message_Id()
    {
        var inboxOptions = new AbpDistributedEventBusOptions();
        inboxOptions.Inboxes.Add(
            "MessageIdentity",
            new InboxConfig("MessageIdentity")
            {
                DatabaseName = "MessageIdentity",
                ImplementationType = typeof(InMemoryEventInbox)
            });

        using var eventBus = ActivatorUtilities.CreateInstance<CapturingNatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = $"MessageIdentity_{Guid.NewGuid():N}",
                SubjectPrefix = $"{Guid.NewGuid():N}.TrueParser.MessageIdentity.Events",
                ClientName = "InboxDeduplication"
            }),
            Options.Create(inboxOptions));

        var eventData = new DynamicEventData("MessageIdentity.Duplicate", new { Value = 4 });
        var firstAdded = await eventBus.AddToInboxForTestAsync(
            "stable-message-id",
            eventData.EventName,
            typeof(DynamicEventData),
            eventData,
            "correlation-id");
        var secondAdded = await eventBus.AddToInboxForTestAsync(
            "stable-message-id",
            eventData.EventName,
            typeof(DynamicEventData),
            eventData,
            "correlation-id");

        firstAdded.ShouldBeTrue();
        secondAdded.ShouldBeTrue();
        GetRequiredService<InMemoryEventInbox>().EnqueueCount.ShouldBe(1);
    }

    [NatsFact]
    public async Task Different_ClientNames_Should_Create_Independent_Consumers_And_Receive_The_Event()
    {
        var streamName = $"Identity_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Identity.Events";
        var eventName = "Order.Created";
        var clientNameA = "Billing-Service";
        var clientNameB = "Notification-Service";

        using var eventBusA = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = clientNameA
            }));
        using var eventBusB = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = clientNameB
            }));

        var receivedByA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedByB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscriptionA = eventBusA.Subscribe(
            eventName,
            new RetainedEventHandler(_ => receivedByA.TrySetResult()));
        using var subscriptionB = eventBusB.Subscribe(
            eventName,
            new RetainedEventHandler(_ => receivedByB.TrySetResult()));

        await eventBusA.InitializeAsync();
        await eventBusB.InitializeAsync();

        var js = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var consumerNameA = System.Text.RegularExpressions.Regex.Replace(
            $"{streamName}_{clientNameA}_{eventName}",
            @"[^a-zA-Z0-9\-_]",
            "_");
        var consumerNameB = System.Text.RegularExpressions.Regex.Replace(
            $"{streamName}_{clientNameB}_{eventName}",
            @"[^a-zA-Z0-9\-_]",
            "_");

        var consumersReady = false;
        for (var iteration = 0; iteration < 50 && !consumersReady; iteration++)
        {
            try
            {
                await js.GetConsumerAsync(streamName, consumerNameA);
                await js.GetConsumerAsync(streamName, consumerNameB);
                consumersReady = true;
            }
            catch (NatsJSApiException)
            {
                await Task.Delay(100);
            }
        }

        consumersReady.ShouldBeTrue("each ClientName must create its own durable consumer");

        await eventBusA.PublishAsync(
            typeof(DynamicEventData),
            new DynamicEventData(eventName, new { Value = 1 }),
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await Task.WhenAll(
            receivedByA.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [NatsFact]
    public async Task Same_ClientName_Should_Share_One_Durable_And_Deliver_Once()
    {
        var streamName = $"Identity_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Identity.Events";
        var eventName = "Order.Updated";
        var clientName = "Billing-Service";

        using var eventBusA = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = clientName
            }));
        using var eventBusB = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = clientName
            }));

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryCount = 0;
        using var subscriptionA = eventBusA.Subscribe(
            eventName,
            new RetainedEventHandler(_ =>
            {
                Interlocked.Increment(ref deliveryCount);
                received.TrySetResult();
            }));
        using var subscriptionB = eventBusB.Subscribe(
            eventName,
            new RetainedEventHandler(_ =>
            {
                Interlocked.Increment(ref deliveryCount);
                received.TrySetResult();
            }));

        await eventBusA.InitializeAsync();
        await eventBusB.InitializeAsync();

        var js = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var consumerName = System.Text.RegularExpressions.Regex.Replace(
            $"{streamName}_{clientName}_{eventName}",
            @"[^a-zA-Z0-9\-_]",
            "_");

        var consumerReady = false;
        for (var iteration = 0; iteration < 50 && !consumerReady; iteration++)
        {
            try
            {
                await js.GetConsumerAsync(streamName, consumerName);
                consumerReady = true;
            }
            catch (NatsJSApiException)
            {
                await Task.Delay(100);
            }
        }

        consumerReady.ShouldBeTrue("replicas with the same ClientName must share one durable consumer");

        await eventBusA.PublishAsync(
            typeof(DynamicEventData),
            new DynamicEventData(eventName, new { Value = 1 }),
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        Volatile.Read(ref deliveryCount).ShouldBe(1);
    }

    [NatsFact]
    public async Task EventBus_ClientName_Should_Not_Fall_Back_To_AbpNats_ClientName()
    {
        using var eventBus = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = $"Identity_{Guid.NewGuid():N}",
                SubjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Identity.Events"
            }));

        var exception = await Should.ThrowAsync<AbpException>(() => eventBus.InitializeAsync());
        exception.Message.ShouldContain("TrueParser:EventBus:Nats:ClientName is required");
    }

    [NatsFact]
    public async Task Missing_ClientName_Should_Fail_During_Initialization()
    {
        using var eventBus = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = $"Identity_{Guid.NewGuid():N}",
                SubjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Identity.Events"
            }));

        var exception = await Should.ThrowAsync<AbpException>(() => eventBus.InitializeAsync());
        exception.Message.ShouldContain("TrueParser:EventBus:Nats:ClientName is required");
    }

    [NatsFact]
    public async Task Invalid_ClientName_Should_Fail_During_Initialization()
    {
        using var eventBus = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = $"Identity_{Guid.NewGuid():N}",
                SubjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Identity.Events",
                ClientName = "!!!"
            }));

        var exception = await Should.ThrowAsync<AbpException>(() => eventBus.InitializeAsync());
        exception.Message.ShouldContain("must contain at least one letter or digit");
    }
}

[EventName("TestEvent")]
public class TestEventData
{
    public string? Message { get; set; }
}

public class WildcardTestHandler : IDistributedEventHandler<DynamicEventData>
{
    private readonly Action<DynamicEventData> _onReceived;

    public WildcardTestHandler(Action<DynamicEventData> onReceived) => _onReceived = onReceived;

    public Task HandleEventAsync(DynamicEventData eventData)
    {
        _onReceived(eventData);
        return Task.CompletedTask;
    }
}

public class RetainedEventHandler : IDistributedEventHandler<DynamicEventData>
{
    private readonly Action<DynamicEventData> _onReceived;

    public RetainedEventHandler(Action<DynamicEventData> onReceived) => _onReceived = onReceived;

    public Task HandleEventAsync(DynamicEventData eventData)
    {
        _onReceived(eventData);
        return Task.CompletedTask;
    }
}

public class TestEventHandler : IDistributedEventHandler<TestEventData>
{
    private readonly Action<TestEventData> _onReceived;

    public TestEventHandler(Action<TestEventData> onReceived) => _onReceived = onReceived;

    public Task HandleEventAsync(TestEventData eventData)
    {
        _onReceived(eventData);
        return Task.CompletedTask;
    }
}

public sealed class CapturingNatsDistributedEventBus : NatsDistributedEventBus
{
    public string? LastMessageId { get; private set; }
    public string? LastCorrelationId { get; private set; }

    public CapturingNatsDistributedEventBus(
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
            natsOptions,
            jetStreamContextAccessor,
            serializer,
            serviceScopeFactory,
            distributedEventBusOptions,
            currentTenant,
            unitOfWorkManager,
            guidGenerator,
            clock,
            eventHandlerInvoker,
            localEventBus,
            correlationIdProvider,
            logger)
    {
    }

    protected override Task<bool> AddToInboxAsync(
        string? messageId,
        string eventName,
        Type eventType,
        object eventData,
        string? correlationId)
    {
        LastMessageId = messageId;
        LastCorrelationId = correlationId;
        return Task.FromResult(false);
    }

    public Task<bool> AddToInboxForTestAsync(
        string? messageId,
        string eventName,
        Type eventType,
        object eventData,
        string? correlationId)
    {
        return base.AddToInboxAsync(messageId, eventName, eventType, eventData, correlationId);
    }

    public Task ProcessFromInboxForTestAsync(IncomingEventInfo incomingEvent, InboxConfig inboxConfig)
    {
        return base.ProcessFromInboxAsync(incomingEvent, inboxConfig);
    }
}

public sealed class InMemoryEventInbox : IEventInbox
{
    private readonly List<IncomingEventInfo> _events = new();
    private readonly object _syncRoot = new();

    public int EnqueueCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _events.Count;
            }
        }
    }

    public Task EnqueueAsync(IncomingEventInfo incomingEvent)
    {
        lock (_syncRoot)
        {
            _events.Add(incomingEvent);
        }

        return Task.CompletedTask;
    }

    public Task<List<IncomingEventInfo>> GetWaitingEventsAsync(
        int maxCount,
        Expression<Func<IIncomingEventInfo, bool>>? filter = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_events.Take(maxCount).ToList());
        }
    }

    public Task MarkAsProcessedAsync(Guid id) => Task.CompletedTask;

    public Task RetryLaterAsync(Guid id, int retryCount, DateTime? nextRetryTime) => Task.CompletedTask;

    public Task MarkAsDiscardAsync(Guid id) => Task.CompletedTask;

    public Task<bool> ExistsByMessageIdAsync(string messageId)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_events.Any(eventInfo => eventInfo.MessageId == messageId));
        }
    }

    public Task DeleteOldEventsAsync() => Task.CompletedTask;
}
