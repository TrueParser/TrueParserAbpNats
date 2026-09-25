using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NSubstitute;
using TrueParser.Abp.Nats;
using Xunit;
using AbpNatsConnectionPool = TrueParser.Abp.Nats.NatsConnectionPool;
using AbpNatsConnectionPoolInterface = TrueParser.Abp.Nats.INatsConnectionPool;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsConnectionPoolShutdownTests
{
    [Fact]
    public async Task Async_ServiceProvider_Disposal_Should_Dispose_The_Connection_Pool()
    {
        var connection = Substitute.For<INatsConnection>();
        var pool = new TestConnectionPool(connection);
        var services = new ServiceCollection();
        services.AddSingleton<AbpNatsConnectionPoolInterface>(_ => pool);
        await using (var provider = services.BuildServiceProvider())
        {
            var resolvedPool = provider.GetRequiredService<AbpNatsConnectionPoolInterface>();
            await resolvedPool.GetAsync();
        }

        await connection.Received(1).DisposeAsync();
    }

    private sealed class TestConnectionPool(INatsConnection connection)
        : AbpNatsConnectionPool(Options.Create(new AbpNatsOptions()))
    {
        protected override INatsConnection CreateConnection(string connectionName) => connection;
    }
}
