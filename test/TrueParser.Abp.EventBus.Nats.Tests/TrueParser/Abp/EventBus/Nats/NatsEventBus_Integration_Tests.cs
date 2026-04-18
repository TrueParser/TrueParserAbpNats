using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Text.Json;
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
