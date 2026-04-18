using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Volo.Abp.DependencyInjection;

namespace TrueParser.Abp.Nats;

public class JetStreamContextAccessor : IJetStreamContextAccessor, ISingletonDependency
{
    private readonly INatsConnectionPool _connectionPool;
    private readonly ConcurrentDictionary<string, (INatsConnection Connection, INatsJSContext Context)> _contexts = new();

    public JetStreamContextAccessor(INatsConnectionPool connectionPool)
    {
        _connectionPool = connectionPool;
    }

    public async ValueTask<INatsJSContext> GetContextAsync(string? connectionName = null)
    {
        var cacheKey = connectionName ?? "Default";
        var connection = await _connectionPool.GetAsync(connectionName);

        if (_contexts.TryGetValue(cacheKey, out var cached) && ReferenceEquals(cached.Connection, connection))
        {
            return cached.Context;
        }

        // Connection object is new or was replaced — create a fresh JetStream context.
        var context = connection.CreateJetStreamContext();
        _contexts[cacheKey] = (connection, context);
        return context;
    }
}
