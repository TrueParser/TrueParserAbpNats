using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TrueParser.Abp.Nats;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
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
        var receivedAllValues = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        
        // Subject prefix is TrueParser.Test.Events
        // This subscription should catch any events starting with TrueParser.Test.Events.Wildcard
        using var subscription = _distributedEventBus.Subscribe("Wildcard.*", new WildcardTestHandler(eventData =>
        {
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
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData("Wildcard.First", new { Value = 1 }), onUnitOfWorkComplete: false, useOutbox: false);
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData("Wildcard.Second", new { Value = 2 }), onUnitOfWorkComplete: false, useOutbox: false);
            await _distributedEventBus.PublishAsync(typeof(DynamicEventData), new DynamicEventData("NotWildcard.Something", new { Value = 3 }), onUnitOfWorkComplete: false, useOutbox: false);
            await Task.Delay(100);
            iterations++;
        }

        // Assert
        await receivedAllValues.Task.WaitAsync(TimeSpan.FromSeconds(10));

        receivedValues.ContainsKey(1).ShouldBeTrue();
        receivedValues.ContainsKey(2).ShouldBeTrue();
        receivedValues.ContainsKey(3).ShouldBeFalse();
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
