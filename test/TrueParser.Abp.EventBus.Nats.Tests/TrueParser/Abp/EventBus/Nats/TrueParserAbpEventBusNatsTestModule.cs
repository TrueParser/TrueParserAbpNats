using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using TrueParser.Abp.EventBus.Nats;
using TrueParser.Abp.Nats;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;

namespace TrueParser.Abp.EventBus.Nats;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(TrueParserAbpEventBusNatsModule)
)]
public class TrueParserAbpEventBusNatsTestModule : AbpModule
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var logFile = Path.Combine(Path.GetTempPath(), "TrueParser.Abp.EventBus.Nats.Tests.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logFile, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        context.Services.AddLogging(logging => logging.AddSerilog(Log.Logger, dispose: true));

        Configure<AbpNatsOptions>(options =>
        {
            options.Connections = "nats://localhost:4222";
        });
        
        Configure<NatsDistributedEventBusOptions>(options =>
        {
            options.StreamName = $"TrueParserTestEvents_{RunId}";
            options.SubjectPrefix = $"{RunId}.TrueParser.Test.Events";
        });
    }
}
