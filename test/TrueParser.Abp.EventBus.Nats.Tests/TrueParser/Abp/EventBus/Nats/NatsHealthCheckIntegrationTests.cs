using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using TrueParser.Abp.Nats;
using Xunit;
using AbpNatsConnectionPool = TrueParser.Abp.Nats.NatsConnectionPool;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsHealthCheckIntegrationTests : NatsEventBusTestBase
{
    [NatsFact]
    public void Module_Should_Register_The_NATS_Health_Check()
    {
        var registrations = GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.ShouldContain(registration => registration.Name == "nats");
    }

    [NatsFact]
    public async Task HealthCheck_Should_Be_Healthy_When_NATS_And_JetStream_Are_Available()
    {
        var options = new AbpNatsOptions
        {
            Connections = Environment.GetEnvironmentVariable("NATS_TEST_URL")
                ?? "nats://localhost:4222",
            ClientName = $"HealthCheck_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));

        var result = await new NatsHealthCheck(pool).CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [NatsFact]
    public async Task HealthCheck_Should_Be_Unhealthy_When_A_Named_Connection_Is_Unreachable()
    {
        var unusedPort = GetFreePort();
        var options = new AbpNatsOptions
        {
            Connections = Environment.GetEnvironmentVariable("NATS_TEST_URL")
                ?? "nats://localhost:4222",
            ClientName = $"HealthCheck_Named_{Guid.NewGuid():N}",
            NamedConnections =
            {
                ["Analytics"] = $"nats://localhost:{unusedPort}"
            }
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));

        var result = await new NatsHealthCheck(pool)
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [NatsFact]
    public async Task HealthCheck_Should_Be_Unhealthy_When_Server_Is_Unreachable()
    {
        var unusedPort = GetFreePort();
        var options = new AbpNatsOptions
        {
            Connections = $"nats://localhost:{unusedPort}",
            ClientName = $"HealthCheck_Unreachable_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await new NatsHealthCheck(pool)
            .CheckHealthAsync(new HealthCheckContext(), timeout.Token);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    [NatsFact]
    public async Task HealthCheck_Should_Be_Unhealthy_When_NATS_Is_Running_Without_JetStream()
    {
        await using var server = await CoreNatsServer.CreateAsync();
        var options = new AbpNatsOptions
        {
            Connections = server.Url,
            ClientName = $"HealthCheck_CoreOnly_{Guid.NewGuid():N}"
        };
        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await new NatsHealthCheck(pool)
            .CheckHealthAsync(new HealthCheckContext(), timeout.Token);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class CoreNatsServer : IAsyncDisposable
    {
        private readonly string _distro;
        private readonly string _serverBinary;
        private readonly string _storagePath;
        private Process? _process;

        private CoreNatsServer(int port, string distro, string serverBinary, string storagePath)
        {
            Port = port;
            _distro = distro;
            _serverBinary = serverBinary;
            _storagePath = storagePath;
        }

        public int Port { get; }
        public string Url => $"nats://localhost:{Port}";

        public static async Task<CoreNatsServer> CreateAsync()
        {
            var server = new CoreNatsServer(
                GetFreePort(),
                Environment.GetEnvironmentVariable("NATS_TEST_WSL_DISTRO") ?? "ubuntu",
                Environment.GetEnvironmentVariable("NATS_TEST_SERVER_BINARY") ?? "/usr/local/bin/nats-server",
                $"/tmp/trueparser-nats-core-health-{Guid.NewGuid():N}");
            await server.StartAsync();
            return server;
        }

        private async Task StartAsync()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(_distro);
            startInfo.ArgumentList.Add("--exec");
            startInfo.ArgumentList.Add(_serverBinary);
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(Port.ToString());
            startInfo.ArgumentList.Add("-sd");
            startInfo.ArgumentList.Add(_storagePath);

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start core-only NATS for the health-check test.");
            await WaitForPortAsync(expectedOpen: true, TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync()
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

            await WaitForPortAsync(expectedOpen: false, TimeSpan.FromSeconds(15));
            _process?.Dispose();
        }

        private async Task WaitForPortAsync(bool expectedOpen, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (await IsPortOpenAsync() != expectedOpen)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"NATS core server port {Port} did not reach expected state.");
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
    }
}
