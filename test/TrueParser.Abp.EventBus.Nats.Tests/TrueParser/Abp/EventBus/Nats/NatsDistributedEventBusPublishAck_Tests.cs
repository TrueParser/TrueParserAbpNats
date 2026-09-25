using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NSubstitute;
using Shouldly;
using TrueParser.Abp.Nats;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Tracing;
using Volo.Abp.Uow;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsDistributedEventBusPublishAck_Tests
{
    [Fact]
    public async Task Outbox_publish_should_throw_when_JetStream_rejects_the_publish_ack()
    {
        var (eventBus, _) = CreateEventBus(CreateRejectedAck());
        using (eventBus)
        {
            var outgoingEvent = CreateOutgoingEvent();

            var exception = await Should.ThrowAsync<NatsJSApiException>(
                () => eventBus.PublishFromOutboxAsync(outgoingEvent, new OutboxConfig("PublishAck")));

            exception.Message.ShouldContain("publish rejected");
        }
    }

    [Fact]
    public async Task Outbox_publish_should_not_raise_sent_notification_when_JetStream_rejects_the_ack()
    {
        var (eventBus, _) = CreateEventBus(CreateRejectedAck());
        using (eventBus)
        {
            await Record.ExceptionAsync(
                () => eventBus.PublishFromOutboxAsync(CreateOutgoingEvent(), new OutboxConfig("PublishAck")));

            eventBus.DistributedEventSentCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Normal_publish_should_throw_when_JetStream_rejects_the_publish_ack()
    {
        var (eventBus, _) = CreateEventBus(CreateRejectedAck());
        using (eventBus)
        {
            await Should.ThrowAsync<NatsJSApiException>(
                () => eventBus.PublishAsync(
                    typeof(DynamicEventData),
                    new DynamicEventData("PublishAck.Normal", new { Value = 1 }),
                    onUnitOfWorkComplete: false,
                    useOutbox: false));
        }
    }

    [Fact]
    public async Task Normal_and_outbox_successful_publishes_should_use_the_accepted_ack()
    {
        var (eventBus, jetStream) = CreateEventBus(new PubAckResponse { Stream = "PublishAck", Seq = 1 });
        using (eventBus)
        {
            await eventBus.PublishAsync(
                typeof(DynamicEventData),
                new DynamicEventData("PublishAck.Normal", new { Value = 1 }),
                onUnitOfWorkComplete: false,
                useOutbox: false);
            await eventBus.PublishFromOutboxAsync(
                CreateOutgoingEvent(),
                new OutboxConfig("PublishAck"));

            await jetStream.Received(2).PublishAsync<byte[]>(
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<INatsSerialize<byte[]>?>(),
                Arg.Any<NatsJSPubOpts?>(),
                Arg.Any<NatsHeaders?>(),
                Arg.Any<CancellationToken>());
            eventBus.DistributedEventSentCount.ShouldBe(2);
        }
    }

    private static (PublishAckEventBus EventBus, INatsJSContext JetStream) CreateEventBus(PubAckResponse response)
    {
        var jetStream = Substitute.For<INatsJSContext>();
        jetStream.PublishAsync<byte[]>(
                Arg.Any<string>(),
                Arg.Any<byte[]>(),
                Arg.Any<INatsSerialize<byte[]>?>(),
                Arg.Any<NatsJSPubOpts?>(),
                Arg.Any<NatsHeaders?>(),
                Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(response));

        var accessor = Substitute.For<IJetStreamContextAccessor>();
        accessor.GetContextAsync(Arg.Any<string?>())
            .Returns(ValueTask.FromResult(jetStream));

        var serializer = Substitute.For<INatsEventSerializer>();
        serializer.Serialize(Arg.Any<object>()).Returns([1, 2, 3]);

        return (new PublishAckEventBus(accessor, serializer), jetStream);
    }

    private static PubAckResponse CreateRejectedAck()
    {
        return new PubAckResponse
        {
            Error = new ApiError { Code = 500, ErrCode = 100, Description = "publish rejected" }
        };
    }

    private static OutgoingEventInfo CreateOutgoingEvent()
    {
        return new OutgoingEventInfo(
            Guid.NewGuid(),
            "PublishAck.Outbox",
            [1, 2, 3],
            DateTime.UtcNow);
    }

    private sealed class PublishAckEventBus : NatsDistributedEventBus
    {
        public PublishAckEventBus(IJetStreamContextAccessor accessor, INatsEventSerializer serializer)
            : base(
                Options.Create(new NatsDistributedEventBusOptions
                {
                    StreamName = "PublishAck",
                    SubjectPrefix = "PublishAck.Events",
                    ClientName = "PublishAckTests"
                }),
                accessor,
                serializer,
                Substitute.For<IServiceScopeFactory>(),
                Options.Create(new AbpDistributedEventBusOptions()),
                Substitute.For<ICurrentTenant>(),
                Substitute.For<IUnitOfWorkManager>(),
                Substitute.For<IGuidGenerator>(),
                Substitute.For<IClock>(),
                Substitute.For<IEventHandlerInvoker>(),
                Substitute.For<ILocalEventBus>(),
                Substitute.For<ICorrelationIdProvider>(),
                Substitute.For<ILogger<NatsDistributedEventBus>>())
        {
        }

        public int DistributedEventSentCount { get; private set; }

        protected override Task EnsureStreamExistsAsync() => Task.CompletedTask;

        public override Task TriggerDistributedEventSentAsync(DistributedEventSent eventData)
        {
            DistributedEventSentCount++;
            return Task.CompletedTask;
        }
    }
}
