using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;

namespace TrueParser.Abp.Nats;

[DependsOn(
    typeof(AbpThreadingModule)
)]
public class TrueParserAbpNatsModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        Configure<AbpNatsOptions>(configuration.GetSection("TrueParser:Nats"));

        context.Services.AddSingleton<INatsConnectionPool, NatsConnectionPool>();
    }
}
