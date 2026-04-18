using System.Threading.Tasks;
using NATS.Client.JetStream;

namespace TrueParser.Abp.Nats;

public interface IJetStreamContextAccessor
{
    ValueTask<INatsJSContext> GetContextAsync(string? connectionName = null);
}
