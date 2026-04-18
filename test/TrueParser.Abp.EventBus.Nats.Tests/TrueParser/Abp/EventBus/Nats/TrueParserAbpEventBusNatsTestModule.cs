using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using TrueParser.Abp.EventBus.Nats;
using TrueParser.Abp.Nats;

namespace TrueParser.Abp.EventBus.Nats;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(TrueParserAbpEventBusNatsModule)
)]
public class TrueParserAbpEventBusNatsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpNatsOptions>(options =>
        {
            options.Connections = "nats://localhost:4222";
        });
        
        Configure<NatsDistributedEventBusOptions>(options =>
        {
            options.StreamName = "TrueParserTestEvents";
            options.SubjectPrefix = "TrueParser.Test.Events";
        });
    }
}
