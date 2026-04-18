using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.Core;
using NATS.Net;

namespace TrueParser.Abp.Nats;

public class NatsHealthCheck : IHealthCheck
{
    private readonly INatsConnectionPool _connectionPool;

    public NatsHealthCheck(INatsConnectionPool connectionPool)
    {
        _connectionPool = connectionPool;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connectionPool.GetAsync();
            
            // Check connection status
            if (connection.ConnectionState != NatsConnectionState.Open)
            {
                return HealthCheckResult.Unhealthy($"NATS connection is {connection.ConnectionState}");
            }

            // Verify JetStream availability
            var js = connection.CreateJetStreamContext();
            await js.GetAccountInfoAsync(cancellationToken);

            return HealthCheckResult.Healthy("NATS and JetStream are operational.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("NATS health check failed.", ex);
        }
    }
}
