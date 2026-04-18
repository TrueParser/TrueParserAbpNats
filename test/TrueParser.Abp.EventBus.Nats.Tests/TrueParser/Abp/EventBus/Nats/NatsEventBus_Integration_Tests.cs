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

    [Fact]
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

    [Fact]
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
    [Fact]
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
