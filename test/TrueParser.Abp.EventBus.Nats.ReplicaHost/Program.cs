using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TrueParser.Abp.EventBus.Nats;
using TrueParser.Abp.Nats;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: <url> <stream> <subject-prefix> <client-name> <event-name>");
    return 2;
}

var url = args[0];
var streamName = args[1];
var subjectPrefix = args[2];
var clientName = args[3];
var eventName = args[4];

var services = new ServiceCollection();
services.AddApplication<ReplicaHostModule>(options => options.UseAutofac());
await using var serviceProvider = services.BuildServiceProvider();
var application = serviceProvider.GetRequiredService<IAbpApplicationWithExternalServiceProvider>();
await application.InitializeAsync(serviceProvider);

var eventBus = serviceProvider.GetRequiredService<NatsDistributedEventBus>();
var resultFile = Environment.GetEnvironmentVariable("TRUEPARSER_REPLICA_RESULT_FILE")
    ?? throw new InvalidOperationException("TRUEPARSER_REPLICA_RESULT_FILE is required.");
using var subscription = eventBus.Subscribe(
    eventName,
    new ReplicaEventHandler(data =>
    {
        var id = data.Data is JsonElement json &&
            (json.TryGetProperty("Id", out var idProperty) || json.TryGetProperty("id", out idProperty))
            ? idProperty.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            File.AppendAllText(resultFile, id + Environment.NewLine);
        }
    }));

var js = await serviceProvider.GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
var consumerName = GetConsumerName(streamName, clientName, eventName);
for (var attempt = 0; attempt < 100; attempt++)
{
    try
    {
        await js.GetConsumerAsync(streamName, consumerName);
        break;
    }
    catch (Exception) when (attempt < 99)
    {
        await Task.Delay(100);
    }
}

Console.WriteLine("READY");
Console.Out.Flush();
await Console.In.ReadLineAsync();

await application.ShutdownAsync();
return 0;

static string GetConsumerName(string streamName, string clientName, string eventName)
{
    var identity = string.Join("\0", streamName, clientName, eventName);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(identity)))[..10].ToLowerInvariant();
    return System.Text.RegularExpressions.Regex.Replace(
        $"{streamName}_{clientName}_{eventName}_{hash}",
        "[^a-zA-Z0-9\\-_]",
        "_");
}

[DependsOn(typeof(AbpAutofacModule), typeof(TrueParserAbpEventBusNatsModule))]
internal sealed class ReplicaHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<NatsDistributedEventBusOptions>(options =>
        {
            options.StreamName = Environment.GetEnvironmentVariable("REPLICA_STREAM")!;
            options.SubjectPrefix = Environment.GetEnvironmentVariable("REPLICA_SUBJECT_PREFIX")!;
            options.ClientName = Environment.GetEnvironmentVariable("REPLICA_CLIENT_NAME")!;
        });
        Configure<AbpNatsOptions>(options =>
        {
            options.Connections = Environment.GetEnvironmentVariable("REPLICA_URL")!;
            options.ClientName = $"ReplicaConnection_{Environment.ProcessId}";
        });
    }
}

internal sealed class ReplicaEventHandler(Action<DynamicEventData> onReceived)
    : IDistributedEventHandler<DynamicEventData>
{
    public Task HandleEventAsync(DynamicEventData eventData)
    {
        onReceived(eventData);
        return Task.CompletedTask;
    }
}
