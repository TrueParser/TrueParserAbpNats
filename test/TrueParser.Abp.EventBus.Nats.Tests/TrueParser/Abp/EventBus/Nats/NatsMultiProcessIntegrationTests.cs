using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TrueParser.Abp.EventBus.Nats;
using Volo.Abp.EventBus;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsMultiProcessIntegrationTests : NatsEventBusTestBase
{
    [NatsFact]
    public async Task Two_Processes_With_Same_ClientName_Should_Act_As_One_Logical_Service()
    {
        var url = Environment.GetEnvironmentVariable("NATS_TEST_URL") ?? "nats://localhost:4222";
        var streamName = $"MultiProcess_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.MultiProcess.Events";
        var eventName = "Billing.InvoiceIssued";
        var clientName = "Billing";
        var resultDirectory = Path.Combine(Path.GetTempPath(), $"trueparser-replica-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultDirectory);

        await using var processA = await ReplicaProcess.StartAsync(
            url, streamName, subjectPrefix, clientName, eventName,
            Path.Combine(resultDirectory, "a.txt"));
        await using var processB = await ReplicaProcess.StartAsync(
            url, streamName, subjectPrefix, clientName, eventName,
            Path.Combine(resultDirectory, "b.txt"));

        using var publisher = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = "CoveragePublisher",
                ConnectionName = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value.ConnectionName
            }));
        await publisher.InitializeAsync();

        var expectedIds = Enumerable.Range(1, 50)
            .Select(index => $"event-{index:D2}-{Guid.NewGuid():N}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in expectedIds)
        {
            await publisher.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Id = id }),
                onUnitOfWorkComplete: false,
                useOutbox: false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && ReadIds(resultDirectory).Count < expectedIds.Count)
        {
            await Task.Delay(100);
        }

        var handledIds = ReadIds(resultDirectory);
        handledIds.Count.ShouldBe(expectedIds.Count);
        handledIds.SetEquals(expectedIds).ShouldBeTrue();

        await processA.StopAsync();
        await processB.StopAsync();

        try
        {
            Directory.Delete(resultDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The temporary result files are disposable test artifacts.
        }
    }

    [NatsFact]
    public async Task Two_Processes_With_Different_ClientNames_Should_Each_Receive_Full_Event_Set()
    {
        var url = Environment.GetEnvironmentVariable("NATS_TEST_URL") ?? "nats://localhost:4222";
        var streamName = $"MultiProcessFanOut_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.MultiProcessFanOut.Events";
        var eventName = "Billing.InvoiceIssued";
        var resultDirectory = Path.Combine(Path.GetTempPath(), $"trueparser-fanout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(resultDirectory);

        await using var processA = await ReplicaProcess.StartAsync(
            url, streamName, subjectPrefix, "Billing", eventName,
            Path.Combine(resultDirectory, "billing.txt"));
        await using var processB = await ReplicaProcess.StartAsync(
            url, streamName, subjectPrefix, "Notifications", eventName,
            Path.Combine(resultDirectory, "notifications.txt"));

        using var publisher = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = "CoverageFanOutPublisher",
                ConnectionName = GetRequiredService<IOptions<NatsDistributedEventBusOptions>>().Value.ConnectionName
            }));
        await publisher.InitializeAsync();

        var expectedIds = Enumerable.Range(1, 50)
            .Select(index => $"fanout-{index:D2}-{Guid.NewGuid():N}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in expectedIds)
        {
            await publisher.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData(eventName, new { Id = id }),
                onUnitOfWorkComplete: false,
                useOutbox: false);
        }

        var billingFile = Path.Combine(resultDirectory, "billing.txt");
        var notificationsFile = Path.Combine(resultDirectory, "notifications.txt");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline &&
               (ReadFileIds(billingFile).Count < expectedIds.Count ||
                ReadFileIds(notificationsFile).Count < expectedIds.Count))
        {
            await Task.Delay(100);
        }

        var billingIds = ReadFileIds(billingFile);
        var notificationIds = ReadFileIds(notificationsFile);
        billingIds.Count.ShouldBe(expectedIds.Count);
        notificationIds.Count.ShouldBe(expectedIds.Count);
        billingIds.SetEquals(expectedIds).ShouldBeTrue();
        notificationIds.SetEquals(expectedIds).ShouldBeTrue();

        await processA.StopAsync();
        await processB.StopAsync();

        try
        {
            Directory.Delete(resultDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The temporary result files are disposable test artifacts.
        }
    }

    private static HashSet<string> ReadIds(string directory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.txt"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ids.Add(line.Trim());
                }
            }
        }

        return ids;
    }

    private static HashSet<string> ReadFileIds(string file)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return ids;
        }

        foreach (var line in File.ReadLines(file))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ids.Add(line.Trim());
            }
        }

        return ids;
    }

    private sealed class ReplicaProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();

        private ReplicaProcess(Process process)
        {
            _process = process;
        }

        public static async Task<ReplicaProcess> StartAsync(
            string url,
            string streamName,
            string subjectPrefix,
            string clientName,
            string eventName,
            string resultFile)
        {
            var repoRoot = FindRepositoryRoot();
            var hostDll = Path.Combine(
                repoRoot,
                "test",
                "TrueParser.Abp.EventBus.Nats.ReplicaHost",
                "bin",
                "Release",
                "net10.0",
                "TrueParser.Abp.EventBus.Nats.ReplicaHost.dll");
            File.Exists(hostDll).ShouldBeTrue(
                $"Build the solution before running the multi-process test. Missing {hostDll}.");

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            startInfo.ArgumentList.Add(hostDll);
            startInfo.ArgumentList.Add(url);
            startInfo.ArgumentList.Add(streamName);
            startInfo.ArgumentList.Add(subjectPrefix);
            startInfo.ArgumentList.Add(clientName);
            startInfo.ArgumentList.Add(eventName);
            startInfo.Environment["REPLICA_URL"] = url;
            startInfo.Environment["REPLICA_STREAM"] = streamName;
            startInfo.Environment["REPLICA_SUBJECT_PREFIX"] = subjectPrefix;
            startInfo.Environment["REPLICA_CLIENT_NAME"] = clientName;
            startInfo.Environment["TRUEPARSER_REPLICA_RESULT_FILE"] = resultFile;

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the replica host process.");
            var replica = new ReplicaProcess(process);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    return;
                }

                lock (replica._output)
                {
                    replica._output.AppendLine(args.Data);
                }

                if (args.Data == "READY")
                {
                    ready.TrySetResult();
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (replica._output)
                    {
                        replica._output.AppendLine(args.Data);
                    }
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
                await replica.DisposeAsync();
                throw new InvalidOperationException(
                    $"Replica process did not become ready. Output: {replica.GetOutput()}");
            }

            return replica;
        }

        public async Task StopAsync()
        {
            if (_process.HasExited)
            {
                return;
            }

            await _process.StandardInput.WriteLineAsync("STOP");
            await _process.StandardInput.FlushAsync();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            catch
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private string GetOutput()
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TrueParser.Abp.Nats.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the TrueParser.Abp.Nats repository root.");
        }
    }
}
