using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Net;

namespace TrueParser.Abp.Nats;

public class NatsHealthCheck : IHealthCheck
{
    private readonly INatsConnectionPool _connectionPool;
    private readonly AbpNatsOptions _options;

    public NatsHealthCheck(INatsConnectionPool connectionPool)
        : this(connectionPool, Options.Create(new AbpNatsOptions()))
    {
    }

    public NatsHealthCheck(
        INatsConnectionPool connectionPool,
        IOptions<AbpNatsOptions> options)
    {
        _connectionPool = connectionPool;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var connectionName in GetConnectionNames())
            {
                var connection = await _connectionPool.GetAsync(connectionName);

                // A freshly created NATS.Net connection may still be Connecting
                // until its first request is made. Probe JetStream before checking
                // state so a valid fresh connection is not reported unhealthy.
                var js = connection.CreateJetStreamContext();
                await js.GetAccountInfoAsync(cancellationToken);

                if (connection.ConnectionState != NatsConnectionState.Open)
                {
                    return HealthCheckResult.Unhealthy(
                        $"NATS connection '{connectionName}' is {connection.ConnectionState}");
                }
            }

            return HealthCheckResult.Healthy("NATS and JetStream are operational.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("NATS health check failed.", ex);
        }
    }

    private IEnumerable<string> GetConnectionNames()
    {
        yield return "Default";

        foreach (var connectionName in _options.NamedConnections.Keys)
        {
            if (!string.Equals(connectionName, "Default", StringComparison.Ordinal))
            {
                yield return connectionName;
            }
        }
    }
}
