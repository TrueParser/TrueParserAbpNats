using System;
using System.Collections.Generic;
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
    /// Maximum time a delivered message may remain unacknowledged before
    /// JetStream attempts redelivery. Null preserves the server default.
    /// </summary>
    public TimeSpan? AckWait { get; set; }

    /// <summary>
    /// Maximum number of delivery attempts for one message. Null preserves the
    /// server default of unlimited redelivery.
    /// </summary>
    public long? MaxDeliver { get; set; }

    /// <summary>
    /// Optional JetStream redelivery backoff sequence. Null leaves it unset.
    /// </summary>
    public ICollection<TimeSpan>? BackOff { get; set; }

    /// <summary>
    /// Controls where a newly created durable consumer starts in the stream.
    /// Existing durable consumers resume from their stored position.
    /// </summary>
    public ConsumerConfigDeliverPolicy InitialDeliveryPolicy { get; set; } = ConsumerConfigDeliverPolicy.New;

    public NatsDistributedEventBusOptions()
    {
    }
}
