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

public sealed class NatsEnvironmentFactAttribute : FactAttribute
{
    public NatsEnvironmentFactAttribute(params string[] requiredEnvironmentVariables)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_NATS_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set RUN_NATS_TESTS=true to run NATS integration tests.";
            return;
        }

        var missing = requiredEnvironmentVariables
            .Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            .ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Optional NATS integration fixture is unavailable: {string.Join(", ", missing)}.";
        }
    }
}
