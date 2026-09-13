using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using TrueParser.Abp.Nats;
using Xunit;
using AbpNatsConnectionPool = TrueParser.Abp.Nats.NatsConnectionPool;

namespace TrueParser.Abp.EventBus.Nats;

public sealed class NatsConnectionIntegrationTests
{
    [NatsFact]
    public async Task UsernamePassword_Authentication_With_Valid_Credentials_Should_Connect()
    {
        var options = new AbpNatsOptions
        {
            Connections = RequiredEnvironmentVariable("NATS_AUTH_TEST_URL"),
            UserName = RequiredEnvironmentVariable("NATS_AUTH_TEST_USERNAME"),
            Password = RequiredEnvironmentVariable("NATS_AUTH_TEST_PASSWORD")
        };

        await VerifyJetStreamPublishAndConsumeAsync(options, "UsernamePassword.Valid");
    }

    [NatsFact]
    public async Task UsernamePassword_Authentication_With_Invalid_Credentials_Should_Fail()
    {
        var options = new AbpNatsOptions
        {
            Connections = RequiredEnvironmentVariable("NATS_AUTH_TEST_URL"),
            UserName = RequiredEnvironmentVariable("NATS_AUTH_TEST_USERNAME"),
            Password = RequiredEnvironmentVariable("NATS_AUTH_TEST_PASSWORD") + "-invalid"
        };

        await Should.ThrowAsync<Exception>(async () =>
        {
            await using var pool = new AbpNatsConnectionPool(Options.Create(options));
            var connection = await pool.GetAsync();
            var jetStream = connection.CreateJetStreamContext();
            await jetStream.GetAccountInfoAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        });
    }

    [NatsFact]
    public async Task JwtSeed_Authentication_With_Valid_Test_Credentials_Should_Connect()
    {
        var options = new AbpNatsOptions
        {
            Connections = RequiredEnvironmentVariable("NATS_JWT_TEST_URL"),
            Jwt = RequiredEnvironmentVariable("NATS_JWT_TEST_JWT"),
            Seed = RequiredEnvironmentVariable("NATS_JWT_TEST_SEED")
        };

        await VerifyJetStreamPublishAndConsumeAsync(options, "JwtSeed.Valid");
    }

    [NatsFact]
    public async Task JwtSeed_Authentication_With_Invalid_Seed_Should_Fail()
    {
        var options = new AbpNatsOptions
        {
            Connections = RequiredEnvironmentVariable("NATS_JWT_TEST_URL"),
            Jwt = RequiredEnvironmentVariable("NATS_JWT_TEST_JWT"),
            Seed = RequiredEnvironmentVariable("NATS_JWT_TEST_SEED") + "invalid"
        };

        await Should.ThrowAsync<Exception>(async () =>
        {
            await using var pool = new AbpNatsConnectionPool(Options.Create(options));
            var connection = await pool.GetAsync();
            var jetStream = connection.CreateJetStreamContext();
            await jetStream.GetAccountInfoAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        });
    }

    [NatsFact]
    public async Task Default_Connection_Should_Resolve_Default_Server()
    {
        var streamName = $"ConnectionResolution_Default_{Guid.NewGuid():N}";
        var options = CreateNamedConnectionOptions();

        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        var defaultConnection = await pool.GetAsync();
        var defaultJetStream = defaultConnection.CreateJetStreamContext();
        var secondaryConnection = await pool.GetAsync("Secondary");
        var secondaryJetStream = secondaryConnection.CreateJetStreamContext();

        try
        {
            await defaultJetStream.CreateStreamAsync(new StreamConfig(
                streamName,
                [$"{streamName}.>"]));

            (await StreamExistsAsync(defaultJetStream, streamName)).ShouldBeTrue();
            (await StreamExistsAsync(secondaryJetStream, streamName)).ShouldBeFalse();
        }
        finally
        {
            await DeleteStreamIfPresentAsync(defaultJetStream, streamName);
            await DeleteStreamIfPresentAsync(secondaryJetStream, streamName);
        }
    }

    [NatsFact]
    public async Task Named_Connection_Should_Resolve_Configured_Server()
    {
        var streamName = $"ConnectionResolution_Secondary_{Guid.NewGuid():N}";
        var options = CreateNamedConnectionOptions();

        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        var defaultConnection = await pool.GetAsync();
        var defaultJetStream = defaultConnection.CreateJetStreamContext();
        var secondaryConnection = await pool.GetAsync("Secondary");
        var secondaryJetStream = secondaryConnection.CreateJetStreamContext();

        try
        {
            await secondaryJetStream.CreateStreamAsync(new StreamConfig(
                streamName,
                [$"{streamName}.>"]));

            (await StreamExistsAsync(secondaryJetStream, streamName)).ShouldBeTrue();
            (await StreamExistsAsync(defaultJetStream, streamName)).ShouldBeFalse();
        }
        finally
        {
            await DeleteStreamIfPresentAsync(defaultJetStream, streamName);
            await DeleteStreamIfPresentAsync(secondaryJetStream, streamName);
        }
    }

    private static AbpNatsOptions CreateNamedConnectionOptions()
    {
        return new AbpNatsOptions
        {
            Connections = RequiredEnvironmentVariable("NATS_TEST_URL"),
            NamedConnections =
            {
                ["Secondary"] = RequiredEnvironmentVariable("NATS_SECONDARY_TEST_URL")
            }
        };
    }

    private static async Task VerifyJetStreamPublishAndConsumeAsync(
        AbpNatsOptions options,
        string name)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var streamName = $"Auth_{name.Replace('.', '_')}_{suffix}";
        var subject = $"{streamName}.Event";
        var consumerName = $"{streamName}_Consumer";
        var payload = Guid.NewGuid().ToByteArray();

        await using var pool = new AbpNatsConnectionPool(Options.Create(options));
        var connection = await pool.GetAsync();
        var jetStream = connection.CreateJetStreamContext();

        try
        {
            await jetStream.GetAccountInfoAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await jetStream.CreateStreamAsync(new StreamConfig(streamName, [subject]));
            var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                streamName,
                new ConsumerConfig(consumerName)
                {
                    FilterSubject = subject,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.New
                });

            await jetStream.PublishAsync(subject, payload);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = false;
            await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellation.Token))
            {
                message.Data.ShouldBe(payload);
                await message.AckAsync();
                received = true;
                break;
            }

            received.ShouldBeTrue();
        }
        finally
        {
            await DeleteStreamIfPresentAsync(jetStream, streamName);
        }
    }

    private static async Task<bool> StreamExistsAsync(INatsJSContext jetStream, string streamName)
    {
        try
        {
            await jetStream.GetStreamAsync(streamName);
            return true;
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
            return false;
        }
    }

    private static async Task DeleteStreamIfPresentAsync(INatsJSContext jetStream, string streamName)
    {
        try
        {
            await jetStream.DeleteStreamAsync(streamName);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
        }
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be set for this live integration test.");
        }

        return value;
    }
}
