using System.Threading.Tasks;
using NATS.Client.Core;

namespace TrueParser.Abp.Nats;

public interface INatsConnectionPool
{
    ValueTask<INatsConnection> GetAsync(string? connectionName = null);
}
