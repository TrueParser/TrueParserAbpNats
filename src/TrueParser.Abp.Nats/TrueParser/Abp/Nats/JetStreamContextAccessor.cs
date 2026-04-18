using System.Threading.Tasks;
using NATS.Client.JetStream;
using NATS.Net;
using Volo.Abp.DependencyInjection;

namespace TrueParser.Abp.Nats;

public class JetStreamContextAccessor : IJetStreamContextAccessor, ISingletonDependency
{
    private readonly INatsConnectionPool _connectionPool;

    public JetStreamContextAccessor(INatsConnectionPool connectionPool)
    {
        _connectionPool = connectionPool;
    }

    public async ValueTask<INatsJSContext> GetContextAsync(string? connectionName = null)
    {
        var connection = await _connectionPool.GetAsync(connectionName);
        return connection.CreateJetStreamContext();
    }
}
