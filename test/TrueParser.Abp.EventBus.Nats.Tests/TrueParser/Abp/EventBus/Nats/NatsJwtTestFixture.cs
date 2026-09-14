using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NATS.Jwt;
using NATS.Jwt.Models;
using NATS.NKeys;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

[CollectionDefinition("NATS JWT", DisableParallelization = true)]
public sealed class NatsJwtCollection : ICollectionFixture<NatsJwtTestFixture>
{
}

public sealed class NatsJwtTestFixture : IAsyncLifetime
{
    private Process? _serverProcess;
    private string? _configPath;

    public string Url { get; private set; } = string.Empty;
    public string Jwt { get; private set; } = string.Empty;
    public string Seed { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_NATS_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var operatorKey = KeyPair.CreatePair(PrefixByte.Operator);
        using var accountKey = KeyPair.CreatePair(PrefixByte.Account);
        using var systemAccountKey = KeyPair.CreatePair(PrefixByte.Account);
        using var userKey = KeyPair.CreatePair(PrefixByte.User);

        var operatorPublicKey = operatorKey.GetPublicKey();
        var accountPublicKey = accountKey.GetPublicKey();
        var systemAccountPublicKey = systemAccountKey.GetPublicKey();
        var userPublicKey = userKey.GetPublicKey();
        var operatorClaims = NatsJwt.NewOperatorClaims(operatorPublicKey);
        operatorClaims.Name = "TrueParserAcceptanceOperator";
        operatorClaims.Operator.SystemAccount = systemAccountPublicKey;
        var operatorJwt = NatsJwt.EncodeOperatorClaims(operatorClaims, operatorKey);

        var accountClaims = NatsJwt.NewAccountClaims(accountPublicKey);
        accountClaims.Name = "TrueParserAcceptanceAccount";
        accountClaims.Account.Limits.MemoryStorage = -1;
        accountClaims.Account.Limits.DiskStorage = -1;
        accountClaims.Account.Limits.Streams = -1;
        accountClaims.Account.Limits.Consumer = -1;
        var accountJwt = NatsJwt.EncodeAccountClaims(accountClaims, operatorKey);

        var systemAccountClaims = NatsJwt.NewAccountClaims(systemAccountPublicKey);
        systemAccountClaims.Name = "TrueParserAcceptanceSystemAccount";
        var systemAccountJwt = NatsJwt.EncodeAccountClaims(systemAccountClaims, operatorKey);

        var userClaims = NatsJwt.NewUserClaims(userPublicKey);
        userClaims.Name = "TrueParserAcceptanceUser";
        userClaims.User.IssuerAccount = accountPublicKey;
        userClaims.User.Pub.Allow = [">"];
        userClaims.User.Sub.Allow = [">"];
        userClaims.User.Resp = new NatsResponsePermission { MaxMsgs = -1, Expires = TimeSpan.FromSeconds(1) };
        Jwt = NatsJwt.EncodeUserClaims(userClaims, accountKey);
        Seed = userKey.GetSeed();
        var port = GetFreePort();
        Url = $"nats://localhost:{port}";

        _configPath = Path.Combine(
            Path.GetTempPath(),
            $"trueparser-nats-jwt-{Guid.NewGuid():N}.conf");
        var config = string.Join(
            Environment.NewLine,
            $"port: {port}",
            $"operator: {operatorJwt}",
            $"system_account: {systemAccountPublicKey}",
            "resolver: MEMORY",
            "resolver_preload: {",
            $"  {accountPublicKey}: {accountJwt}",
            $"  {systemAccountPublicKey}: {systemAccountJwt}",
            "}",
            "jetstream: enabled",
            string.Empty);
        await File.WriteAllTextAsync(_configPath, config, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(Environment.GetEnvironmentVariable("NATS_TEST_WSL_DISTRO") ?? "ubuntu");
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add(Environment.GetEnvironmentVariable("NATS_TEST_SERVER_BINARY") ?? "/usr/local/bin/nats-server");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(ToWslPath(_configPath));

        _serverProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the disposable JWT NATS server.");

        try
        {
            await WaitForPortAsync(port, expectedOpen: true, TimeSpan.FromSeconds(15));
        }
        catch (Exception exception)
        {
            var serverOutput = await StopAndReadServerOutputAsync();
            await DisposeAsync();
            throw new InvalidOperationException(
                $"The disposable JWT NATS server did not start. Server output:{Environment.NewLine}{serverOutput}",
                exception);
        }
    }

    public async Task DisposeAsync()
    {
        if (_serverProcess is { HasExited: false } process)
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

        _serverProcess?.Dispose();
        _serverProcess = null;

        if (_configPath is not null)
        {
            File.Delete(_configPath);
            _configPath = null;
        }
    }

    private async Task<string> StopAndReadServerOutputAsync()
    {
        if (_serverProcess is null)
        {
            return string.Empty;
        }

        if (!_serverProcess.HasExited)
        {
            _serverProcess.Kill(entireProcessTree: true);
            await _serverProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        var standardOutput = await _serverProcess.StandardOutput.ReadToEndAsync();
        var standardError = await _serverProcess.StandardError.ReadToEndAsync();
        return $"STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}";
    }

    private static string ToWslPath(string windowsPath)
    {
        var fullPath = Path.GetFullPath(windowsPath);
        return $"/mnt/{char.ToLowerInvariant(fullPath[0])}{fullPath[2..].Replace('\\', '/') }";
    }

    private static async Task WaitForPortAsync(int port, bool expectedOpen, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        while (true)
        {
            var open = await IsPortOpenAsync(port);
            if (open == expectedOpen)
            {
                return;
            }

            await Task.Delay(100, timeoutSource.Token);
        }
    }

    private static async Task<bool> IsPortOpenAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync("localhost", port).WaitAsync(TimeSpan.FromMilliseconds(500));
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
