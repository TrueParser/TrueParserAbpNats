using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.EventBus;
using Volo.Abp.Modularity;
using TrueParser.Abp.Nats;
using System.Threading.Tasks;

namespace TrueParser.Abp.EventBus.Nats;

[DependsOn(
    typeof(TrueParserAbpNatsModule),
    typeof(AbpEventBusModule)
)]
public class TrueParserAbpEventBusNatsModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        Configure<NatsDistributedEventBusOptions>(configuration.GetSection("TrueParser:EventBus:Nats"));

        context.Services.AddSingleton<INatsEventSerializer, DefaultNatsEventSerializer>();
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        await context.ServiceProvider
            .GetRequiredService<NatsDistributedEventBus>()
            .InitializeAsync();
    }
}
