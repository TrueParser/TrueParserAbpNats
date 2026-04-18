using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NATS.Client.JetStream;
using NATS.Net;
using Volo.Abp.DependencyInjection;

namespace TrueParser.Abp.Nats;

public class JetStreamContextAccessor : IJetStreamContextAccessor, ISingletonDependency
{
    private readonly INatsConnectionPool _connectionPool;
    private readonly ConcurrentDictionary<string, Lazy<INatsJSContext>> _contexts = new();

    public JetStreamContextAccessor(INatsConnectionPool connectionPool)
    {
        _connectionPool = connectionPool;
    }

    public async ValueTask<INatsJSContext> GetContextAsync(string? connectionName = null)
    {
        var cacheKey = connectionName ?? "Default";
        if (_contexts.TryGetValue(cacheKey, out var existing))
        {
            return existing.Value;
        }
        var connection = await _connectionPool.GetAsync(connectionName);
        return _contexts.GetOrAdd(
            cacheKey,
            _ => new Lazy<INatsJSContext>(
                connection.CreateJetStreamContext,
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
