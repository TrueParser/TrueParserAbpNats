using NATS.Client.JetStream.Models;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsDistributedEventBusOptions
{
    public string StreamName { get; set; } = "TrueParserEvents";

    public string SubjectPrefix { get; set; } = "TrueParser.Events";

    public string? ConnectionName { get; set; }

    public StreamConfigRetention Retention { get; set; } = StreamConfigRetention.Interest;

    public int ReplicaCount { get; set; } = 1;

    public string? MaxAge { get; set; }

    public string? PrefetchCount { get; set; }

    public NatsDistributedEventBusOptions()
    {
    }
}
