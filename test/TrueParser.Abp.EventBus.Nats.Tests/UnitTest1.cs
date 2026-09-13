using Xunit;
using NATS.Client.JetStream.Models;
using TrueParser.Abp.EventBus.Nats;

namespace TrueParser.Abp.EventBus.Nats.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {

    }

    [Fact]
    public void Default_event_bus_retention_should_be_interest()
    {
        Assert.Equal(
            StreamConfigRetention.Interest,
            new NatsDistributedEventBusOptions().Retention);
    }
}
