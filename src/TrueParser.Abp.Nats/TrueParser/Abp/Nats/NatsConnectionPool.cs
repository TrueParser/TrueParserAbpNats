using Microsoft.Extensions.Options;
using NATS.Client.Core;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;

namespace TrueParser.Abp.Nats;

public class NatsConnectionPool : INatsConnectionPool, ISingletonDependency, IAsyncDisposable
{
    private readonly AbpNatsOptions _options;
    private readonly ConcurrentDictionary<string, INatsConnection> _connections = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public NatsConnectionPool(IOptions<AbpNatsOptions> options)
    {
        _options = options.Value;
    }

    public async ValueTask<INatsConnection> GetAsync(string? connectionName = null)
    {
        connectionName ??= "Default";

        if (_connections.TryGetValue(connectionName, out var connection))
        {
            return connection;
        }

        await _semaphore.WaitAsync();
        try
        {
            if (_connections.TryGetValue(connectionName, out connection))
            {
                return connection;
            }

            connection = CreateConnection(connectionName);
            _connections.TryAdd(connectionName, connection);
            return connection;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    protected virtual INatsConnection CreateConnection(string connectionName)
    {
        var url = _options.Connections;
        
        if (connectionName != "Default" && _options.NamedConnections.TryGetValue(connectionName, out var namedUrl))
        {
            url = namedUrl;
        }

        var natsOpts = NatsOpts.Default with { Url = url };

        if (!string.IsNullOrEmpty(_options.ClientName))
        {
            natsOpts = natsOpts with { Name = _options.ClientName };
        }

        if (!string.IsNullOrEmpty(_options.UserName))
        {
            natsOpts = natsOpts with { AuthOpts = new NatsAuthOpts { Username = _options.UserName, Password = _options.Password } };
        }
        else if (!string.IsNullOrEmpty(_options.Jwt))
        {
            natsOpts = natsOpts with { AuthOpts = new NatsAuthOpts { Jwt = _options.Jwt, Seed = _options.Seed } };
        }

        return new NatsConnection(natsOpts);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
        _connections.Clear();
    }
}
