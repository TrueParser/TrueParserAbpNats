using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using MySql.Data.MySqlClient;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EntityFrameworkCore.MySQL;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using TrueParser.Abp.Nats;

namespace TrueParser.Abp.EventBus.Nats;

[DependsOn(
    typeof(AbpAutofacModule),
    typeof(AbpTestBaseModule),
    typeof(AbpEntityFrameworkCoreModule),
    typeof(AbpEntityFrameworkCoreMySQLModule),
    typeof(TrueParserAbpEventBusNatsModule)
)]
public class TrueParserAbpEventBoxTestModule : AbpModule
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    private string _mysqlConnection = null!;
    private string _databaseName = null!;

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var logFile = Path.Combine(Path.GetTempPath(), "TrueParser.Abp.EventBus.Nats.EventBox.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(logFile, rollingInterval: RollingInterval.Day)
            .CreateLogger();
        context.Services.AddLogging(logging => logging.AddSerilog(Log.Logger, dispose: true));

        Configure<AbpBackgroundWorkerOptions>(options =>
        {
            options.IsEnabled = true;
        });

        var configuredMysqlConnection = Environment.GetEnvironmentVariable("TRUEPARSER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(configuredMysqlConnection))
        {
            throw new AbpException(
                "TRUEPARSER_TEST_MYSQL_CONNECTION must be set for EF-backed event-box tests.");
        }

        var mysqlConnectionBuilder = new MySqlConnectionStringBuilder(configuredMysqlConnection);
        // All EF-backed coverage uses one disposable database. Event and stream
        // identities remain unique per test host, so rows cannot cross-test.
        _databaseName = "trueparsercoverage";
        mysqlConnectionBuilder.Database = _databaseName;
        _mysqlConnection = mysqlConnectionBuilder.ConnectionString;

        EnsureDatabaseExists(mysqlConnectionBuilder);

        var natsUrl = Environment.GetEnvironmentVariable("NATS_TEST_URL") ?? "nats://localhost:4222";

        context.Services.Replace(
            ServiceDescriptor.Singleton<INatsConnectionPool, SynchronousNatsConnectionPool>());

        // The shared transport test assembly contains a capturing subclass that
        // intentionally bypasses Inbox persistence. EF-backed event-box coverage
        // must resolve the production transport implementation instead.
        context.Services.RemoveAll<IDistributedEventBus>();
        context.Services.RemoveAll<NatsDistributedEventBus>();
        context.Services.AddSingleton<NatsDistributedEventBus>();
        context.Services.AddSingleton<IDistributedEventBus>(serviceProvider =>
            serviceProvider.GetRequiredService<NatsDistributedEventBus>());

        context.Services.AddAbpDbContext<TestEventBoxDbContext>();
        Configure<AbpDbConnectionOptions>(options =>
        {
            options.ConnectionStrings.Default = _mysqlConnection;
        });

        Configure<AbpDbContextOptions>(options =>
        {
            options.UseMySQL<TestEventBoxDbContext>();
        });

        Configure<AbpNatsOptions>(options =>
        {
            options.Connections = natsUrl;
            options.ClientName = $"CoverageConnection_{RunId}";
        });

        Configure<NatsDistributedEventBusOptions>(options =>
        {
            options.StreamName = $"CoverageEventBox_{RunId}";
            options.SubjectPrefix = $"{RunId}.TrueParser.Coverage.Events";
            options.ClientName = $"CoverageEventBox_{RunId}";
        });

        Configure<AbpDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                config.UseDbContext<TestEventBoxDbContext>();
                config.DatabaseName = $"CoverageEventBox_{RunId}";
            });
            options.Inboxes.Configure(config =>
            {
                config.UseDbContext<TestEventBoxDbContext>();
                config.DatabaseName = $"CoverageEventBox_{RunId}";
            });
        });

        Configure<AbpEventBusBoxesOptions>(options =>
        {
            options.PeriodTimeSpan = TimeSpan.FromMilliseconds(100);
            options.BatchPublishOutboxEvents = false;
        });
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        using var scope = context.ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestEventBoxDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }

    private sealed class SynchronousNatsConnectionPool(IOptions<AbpNatsOptions> options)
        : NatsConnectionPool(options), IDisposable
    {
        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private void EnsureDatabaseExists(MySqlConnectionStringBuilder databaseConnectionBuilder)
    {
        var adminConnectionBuilder = new MySqlConnectionStringBuilder(databaseConnectionBuilder.ConnectionString)
        {
            Database = string.Empty
        };

        using var connection = new MySqlConnection(adminConnectionBuilder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS `{_databaseName.Replace("`", "``")}`";
        command.ExecuteNonQuery();
    }
}
