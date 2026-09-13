using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using TrueParser.Abp.Nats;
using Xunit;
using AbpNatsConnectionPool = TrueParser.Abp.Nats.NatsConnectionPool;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsHealthCheckIntegrationTests : NatsEventBusTestBase
{
    [NatsFact]
    public async Task HealthCheck_Should_Be_Healthy_When_NATS_And_JetStream_Are_Available()
    {
        var options = new AbpNatsOptions
        {
            Connections = Environment.GetEnvironmentVariable("NATS_TEST_URL")
                ?? "nats://localhost:4222",
            ClientName = $"HealthCheck_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));

        var result = await new NatsHealthCheck(pool).CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Healthy);
    }
}
