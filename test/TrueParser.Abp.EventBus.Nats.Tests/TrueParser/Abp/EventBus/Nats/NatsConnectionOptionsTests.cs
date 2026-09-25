using NATS.Client.Core;
using Shouldly;
using TrueParser.Abp.Nats;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsConnectionOptionsTests
{
    [Fact]
    public void Abp_NATS_Options_Should_Expose_Custom_TLS_Options()
    {
        typeof(AbpNatsOptions).GetProperties()
            .ShouldContain(property => property.PropertyType == typeof(NatsTlsOpts));
    }
}
