using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using TrueParser.Abp.Nats;
using Volo.Abp;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Xunit;
using AbpNatsConnectionPool = TrueParser.Abp.Nats.NatsConnectionPool;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsRecoveryIntegrationTests : NatsEventBusTestBase
{
    [NatsFact]
    public async Task Consumer_Should_Recover_After_NATS_Server_Restart()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Recovery_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Recovery.Events";
        var eventName = $"Recovery.Restart.{Guid.NewGuid():N}";
        var (eventBus, pool) = CreateEventBus(server.Url, streamName, subjectPrefix);
        var receivedCount = 0;
        var receivedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = eventBus.Subscribe(eventName, new RetainedEventHandler(_ =>
        {
            var count = Interlocked.Increment(ref receivedCount);
            if (count == 1)
            {
                receivedFirst.TrySetResult();
            }
            else if (count == 2)
            {
                receivedSecond.TrySetResult();
            }
        }));

        try
        {
            await eventBus.InitializeAsync();
            await PublishDynamicEventAsync(eventBus, eventName, 1);
            await receivedFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var connection = await pool.GetAsync();
            await server.StopAsync();
            await WaitUntilAsync(
                () => connection.ConnectionState != NatsConnectionState.Open,
                TimeSpan.FromSeconds(10));

            await server.StartAsync();
            await WaitUntilAsync(
                () => connection.ConnectionState == NatsConnectionState.Open,
                TimeSpan.FromSeconds(15));

            // NATS.Net reports the connection open before the consumer's retry
            // loop has necessarily rebound its JetStream consumer.
            await Task.Delay(TimeSpan.FromSeconds(6));
            await PublishDynamicEventAsync(eventBus, eventName, 2);
            await receivedSecond.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Volatile.Read(ref receivedCount).ShouldBe(2);
        }
        finally
        {
            eventBus.Dispose();
            await pool.DisposeAsync();
        }
    }

    [NatsFact]
    public async Task Publish_During_Broker_Outage_Should_Fail_Or_Wait_According_To_NATS_Client_Semantics_Without_False_Success()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Recovery_{Guid.NewGuid():N}";
        var subject = $"{Guid.NewGuid():N}.TrueParser.Recovery.Publish";
        var options = new AbpNatsOptions
        {
            Connections = server.Url,
            ClientName = $"RecoveryConnection_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        var connection = await pool.GetAsync();
        var jetStream = connection.CreateJetStreamContext();

        await jetStream.CreateStreamAsync(new StreamConfig(streamName, [subject]));
        await server.StopAsync();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await jetStream.PublishAsync(subject, Guid.NewGuid().ToByteArray())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
        });

        exception.ShouldNotBeNull("a JetStream publish without a broker ACK must not report success");
    }

    [NatsFact]
    public async Task Durable_Consumer_Should_Resume_Backlog_After_Server_Restart()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Recovery_{Guid.NewGuid():N}";
        var subject = $"{Guid.NewGuid():N}.TrueParser.Recovery.Backlog";
        var consumerName = $"Recovery_{Guid.NewGuid():N}";
        var payload = Guid.NewGuid().ToByteArray();
        var options = new AbpNatsOptions
        {
            Connections = server.Url,
            ClientName = $"RecoveryConnection_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        var connection = await pool.GetAsync();
        var jetStream = connection.CreateJetStreamContext();

        await jetStream.CreateStreamAsync(new StreamConfig(streamName, [subject]));
        await jetStream.CreateOrUpdateConsumerAsync(
            streamName,
            new ConsumerConfig(consumerName)
            {
                FilterSubject = subject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.New
            });
        await jetStream.PublishAsync(subject, payload);

        await server.RestartAsync();

        var resumedConsumer = await WaitForConsumerAsync(jetStream, streamName, consumerName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = false;
        await foreach (var message in resumedConsumer.ConsumeAsync<byte[]>(cancellationToken: cancellation.Token))
        {
            message.Data.ShouldBe(payload);
            await message.AckAsync();
            received = true;
            break;
        }

        received.ShouldBeTrue("the existing durable must deliver its retained unacknowledged backlog");
    }

    [NatsFact]
    public async Task Application_Shutdown_Should_Stop_Consumers_Without_Hanging()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Shutdown_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Shutdown.Events";
        var eventName = $"Shutdown.Active.{Guid.NewGuid():N}";
        var (eventBus, pool) = CreateEventBus(server.Url, streamName, subjectPrefix);

        using var subscription = eventBus.Subscribe(eventName, new RetainedEventHandler(_ => { }));
        try
        {
            await eventBus.InitializeAsync();
            await PublishDynamicEventAsync(eventBus, eventName, 1);

            await Task.Run(async () =>
            {
                await eventBus.OnApplicationShutdownAsync(new ApplicationShutdownContext(ServiceProvider));
                eventBus.Dispose();
                await pool.DisposeAsync();
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            eventBus.Dispose();
            await pool.DisposeAsync();
        }
    }

    [NatsFact]
    public async Task Disposed_EventBus_Should_No_Longer_Consume_Messages()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Shutdown_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Shutdown.Events";
        var eventName = $"Shutdown.Disposed.{Guid.NewGuid():N}";
        var oldInvocations = 0;
        var newReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new NatsDistributedEventBusOptions
        {
            StreamName = streamName,
            SubjectPrefix = subjectPrefix,
            ClientName = $"Shutdown_{Guid.NewGuid():N}"
        };

        var (oldBus, oldPool) = CreateEventBus(server.Url, options);
        var oldSubscription = oldBus.Subscribe(eventName, new RetainedEventHandler(_ =>
            Interlocked.Increment(ref oldInvocations)));

        try
        {
            await oldBus.InitializeAsync();
            await PublishDynamicEventAsync(oldBus, eventName, 1);
            await WaitUntilAsync(() => Volatile.Read(ref oldInvocations) == 1, TimeSpan.FromSeconds(10));

            oldBus.Dispose();
            oldSubscription.Dispose();
            await oldPool.DisposeAsync();

            var (newBus, newPool) = CreateEventBus(server.Url, options);
            using var newSubscription = newBus.Subscribe(eventName, new RetainedEventHandler(_ =>
                newReceived.TrySetResult()));
            try
            {
                await newBus.InitializeAsync();
                await PublishDynamicEventAsync(newBus, eventName, 2);
                await newReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(500);
                Volatile.Read(ref oldInvocations).ShouldBe(1);
            }
            finally
            {
                newBus.Dispose();
                await newPool.DisposeAsync();
            }
        }
        finally
        {
            oldSubscription.Dispose();
            oldBus.Dispose();
            await oldPool.DisposeAsync();
        }
    }

    [NatsFact]
    public async Task Fresh_EventBus_Should_Start_After_Previous_Bus_Was_Shut_Down()
    {
        await using var server = await ManagedNatsServer.CreateAsync();
        var streamName = $"Shutdown_{Guid.NewGuid():N}";
        var subjectPrefix = $"{Guid.NewGuid():N}.TrueParser.Shutdown.Events";
        var eventName = $"Shutdown.Fresh.{Guid.NewGuid():N}";
        var options = new NatsDistributedEventBusOptions
        {
            StreamName = streamName,
            SubjectPrefix = subjectPrefix,
            ClientName = $"Shutdown_{Guid.NewGuid():N}"
        };

        var (oldBus, oldPool) = CreateEventBus(server.Url, options);
        var oldReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldSubscription = oldBus.Subscribe(
            eventName,
            new RetainedEventHandler(_ => oldReceived.TrySetResult()));

        try
        {
            await oldBus.InitializeAsync();
            await PublishDynamicEventAsync(oldBus, eventName, 1);
            await oldReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            oldBus.Dispose();
            await oldPool.DisposeAsync();

            var (freshBus, freshPool) = CreateEventBus(server.Url, options);
            var freshReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var freshSubscription = freshBus.Subscribe(
                eventName,
                new RetainedEventHandler(_ => freshReceived.TrySetResult()));
            try
            {
                await freshBus.InitializeAsync();
                await PublishDynamicEventAsync(freshBus, eventName, 2);
                await freshReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                freshBus.Dispose();
                await freshPool.DisposeAsync();
            }
        }
        finally
        {
            oldBus.Dispose();
            await oldPool.DisposeAsync();
        }
    }

    private (NatsDistributedEventBus EventBus, AbpNatsConnectionPool Pool) CreateEventBus(
        string url,
        string streamName,
        string subjectPrefix)
    {
        return CreateEventBus(
            url,
            new NatsDistributedEventBusOptions
            {
                StreamName = streamName,
                SubjectPrefix = subjectPrefix,
                ClientName = $"Recovery_{Guid.NewGuid():N}"
            });
    }

    private (NatsDistributedEventBus EventBus, AbpNatsConnectionPool Pool) CreateEventBus(
        string url,
        NatsDistributedEventBusOptions eventBusOptions)
    {
        var pool = new AbpNatsConnectionPool(Options.Create(new AbpNatsOptions
        {
            Connections = url,
            ClientName = $"RecoveryConnection_{Guid.NewGuid():N}"
        }));
        var accessor = new JetStreamContextAccessor(pool);
        var eventBus = ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(eventBusOptions),
            accessor);
        return (eventBus, pool);
    }

    private static Task PublishDynamicEventAsync(
        NatsDistributedEventBus eventBus,
        string eventName,
        int value)
    {
        return eventBus.PublishAsync(
            typeof(DynamicEventData),
            new DynamicEventData(eventName, new { Value = value }),
            onUnitOfWorkComplete: false,
            useOutbox: false);
    }

    private static async Task<INatsJSConsumer> WaitForConsumerAsync(
        INatsJSContext jetStream,
        string streamName,
        string consumerName)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return await jetStream.GetConsumerAsync(streamName, consumerName);
            }
            catch (Exception exception)
            {
                lastException = exception;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException(
            $"Consumer '{consumerName}' was not available after broker restart.",
            lastException);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected NATS lifecycle state was not reached.");
            }

            await Task.Delay(100);
        }
    }

    private sealed class ManagedNatsServer : IAsyncDisposable
    {
        private readonly string _distro;
        private readonly string _serverBinary;
        private readonly string _storagePath;
        private Process? _process;

        private ManagedNatsServer(int port, string distro, string serverBinary, string storagePath)
        {
            Port = port;
            _distro = distro;
            _serverBinary = serverBinary;
            _storagePath = storagePath;
        }

        public int Port { get; }

        public string Url => $"nats://localhost:{Port}";

        public static async Task<ManagedNatsServer> CreateAsync()
        {
            var server = new ManagedNatsServer(
                GetFreePort(),
                Environment.GetEnvironmentVariable("NATS_TEST_WSL_DISTRO") ?? "ubuntu",
                Environment.GetEnvironmentVariable("NATS_TEST_SERVER_BINARY") ?? "/usr/local/bin/nats-server",
                $"/tmp/trueparser-nats-recovery-{Guid.NewGuid():N}");
            await server.StartAsync();
            return server;
        }

        public async Task StartAsync()
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _process?.Dispose();
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processStartInfo.ArgumentList.Add("-d");
            processStartInfo.ArgumentList.Add(_distro);
            processStartInfo.ArgumentList.Add("--exec");
            processStartInfo.ArgumentList.Add(_serverBinary);
            processStartInfo.ArgumentList.Add("-js");
            processStartInfo.ArgumentList.Add("-p");
            processStartInfo.ArgumentList.Add(Port.ToString());
            processStartInfo.ArgumentList.Add("-sd");
            processStartInfo.ArgumentList.Add(_storagePath);

            _process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("Could not start wsl.exe for the NATS recovery test.");
            await WaitForPortAsync(expectedOpen: true, TimeSpan.FromSeconds(15));
        }

        public async Task StopAsync()
        {
            if (_process is { HasExited: false } process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }

            await KillMatchingWslServerAsync();
            await WaitForPortAsync(expectedOpen: false, TimeSpan.FromSeconds(15));
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            await StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            catch
            {
                // Preserve the original test failure if the test already failed.
            }

            _process?.Dispose();
        }

        private async Task KillMatchingWslServerAsync()
        {
            var cleanupInfo = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            cleanupInfo.ArgumentList.Add("-d");
            cleanupInfo.ArgumentList.Add(_distro);
            cleanupInfo.ArgumentList.Add("--exec");
            cleanupInfo.ArgumentList.Add("pkill");
            cleanupInfo.ArgumentList.Add("-TERM");
            cleanupInfo.ArgumentList.Add("-f");
            cleanupInfo.ArgumentList.Add(_storagePath);

            using var cleanupProcess = Process.Start(cleanupInfo);
            if (cleanupProcess is not null)
            {
                await cleanupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private async Task WaitForPortAsync(bool expectedOpen, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (await IsPortOpenAsync() != expectedOpen)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"NATS server port {Port} did not reach expected state: open={expectedOpen}.");
                }

                await Task.Delay(100);
            }
        }

        private async Task<bool> IsPortOpenAsync()
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("localhost", Port).WaitAsync(TimeSpan.FromMilliseconds(500));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
