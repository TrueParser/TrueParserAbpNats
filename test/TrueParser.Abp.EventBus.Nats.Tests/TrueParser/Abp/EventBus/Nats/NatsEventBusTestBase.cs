using Volo.Abp;
using Volo.Abp.Testing;

namespace TrueParser.Abp.EventBus.Nats;

public abstract class NatsEventBusTestBase : AbpIntegratedTest<TrueParserAbpEventBusNatsTestModule>
{
    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }
}
