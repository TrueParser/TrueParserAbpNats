using System;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsFactAttribute : FactAttribute
{
    public NatsFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_NATS_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_NATS_TESTS=true to run NATS integration tests.";
        }
    }
}
