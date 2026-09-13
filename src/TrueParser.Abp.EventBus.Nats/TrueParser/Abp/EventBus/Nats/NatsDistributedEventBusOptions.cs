using NATS.Client.JetStream.Models;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsDistributedEventBusOptions
{
    public string StreamName { get; set; } = "TrueParserEvents";

    public string SubjectPrefix { get; set; } = "TrueParser.Events";

    public string? ConnectionName { get; set; }

    /// <summary>
    /// Required stable logical service identity used when naming durable consumers.
    /// This is independent of the NATS connection-layer client name.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Stream retention policy. Interest is the default for distributed-event
    /// fan-out; Workqueue is not supported by the standard event-bus path.
    /// </summary>
    public StreamConfigRetention Retention { get; set; } = StreamConfigRetention.Interest;

    public int ReplicaCount { get; set; } = 1;

    public string? MaxAge { get; set; }

    public string? PrefetchCount { get; set; }

    /// <summary>
    /// Controls where a newly created durable consumer starts in the stream.
    /// Existing durable consumers resume from their stored position.
    /// </summary>
    public ConsumerConfigDeliverPolicy InitialDeliveryPolicy { get; set; } = ConsumerConfigDeliverPolicy.New;

    public NatsDistributedEventBusOptions()
    {
    }
}
