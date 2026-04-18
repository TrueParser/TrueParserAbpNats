using System;
using System.Threading.Tasks;
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
        var messageReceived = false;
        var testData = new TestEventData { Message = "Hello NATS!" };

        // We use a local action handler for testing
        using (var serviceScope = ServiceProvider.CreateScope())
        {
            _distributedEventBus.Subscribe<TestEventData>(async (data) =>
            {
                data.Message.ShouldBe("Hello NATS!");
                messageReceived = true;
                await Task.CompletedTask;
            });

            // Act
            await _distributedEventBus.PublishAsync(testData);

            // Wait for delivery (NATS is fast, but async)
            var iterations = 0;
            while (!messageReceived && iterations < 50)
            {
                await Task.Delay(100);
                iterations++;
            }

            // Assert
            messageReceived.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Should_Subscribe_With_Wildcard()
    {
        // Arrange
        var receivedCount = 0;
        
        // Subject prefix is TrueParser.Test.Events
        // This subscription should catch any events starting with TrueParser.Test.Events.Wildcard
        _distributedEventBus.Subscribe("Wildcard.*", new WildcardTestHandler(() => receivedCount++));

        // Act
        await _distributedEventBus.PublishAsync("Wildcard.First", new { Value = 1 });
        await _distributedEventBus.PublishAsync("Wildcard.Second", new { Value = 2 });
        await _distributedEventBus.PublishAsync("NotWildcard.Something", new { Value = 3 });

        // Wait
        await Task.Delay(1000);

        // Assert
        receivedCount.ShouldBe(2);
    }
}

[EventName("TestEvent")]
public class TestEventData
{
    public string? Message { get; set; }
}

public class WildcardTestHandler : IDistributedEventHandler<DynamicEventData>
{
    private readonly Action _onReceived;

    public WildcardTestHandler(Action onReceived) => _onReceived = onReceived;

    public Task HandleEventAsync(DynamicEventData eventData)
    {
        _onReceived();
        return Task.CompletedTask;
    }
}
