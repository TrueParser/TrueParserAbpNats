using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
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

public sealed class OutboxBatchIntegrationTests : NatsEventBusTestBase
{
    [NatsFact]
    public async Task Auto_Created_Stream_Should_Deduplicate_Repeated_Outbox_Message_Id()
    {
        var options = CreateOptions("OutboxBatch.DefaultDuplicateWindow");
        var eventBus = CreateEventBus(options);
        var jetStream = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var outgoingEvent = CreateEvents("OutboxBatch.DefaultDuplicateWindow", 1).Single();

        try
        {
            await eventBus.InitializeAsync();
            var stream = await jetStream.GetStreamAsync(options.StreamName);
            stream.Info.Config.DuplicateWindow.ShouldBe(TimeSpan.FromMinutes(2));
            _ = await CreateConsumerAsync(jetStream, options, "DefaultDuplicateWindow");

            await eventBus.PublishFromOutboxAsync(outgoingEvent, new OutboxConfig("OutboxBatch"));
            await eventBus.PublishFromOutboxAsync(outgoingEvent, new OutboxConfig("OutboxBatch"));

            stream = await jetStream.GetStreamAsync(options.StreamName);
            stream.Info.State.Messages.ShouldBe(1);
        }
        finally
        {
            eventBus.Dispose();
            await DeleteStreamAsync(jetStream, options.StreamName);
        }
    }

    [NatsFact]
    public async Task PublishManyFromOutbox_Should_Publish_All_Events_With_Original_MessageIds()
    {
        var options = CreateOptions("OutboxBatch.MessageIds");
        var eventBus = CreateEventBus(options);
        var jetStream = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var events = CreateEvents("OutboxBatch.MessageIds", 3);

        try
        {
            await eventBus.InitializeAsync();
            var consumer = await CreateConsumerAsync(jetStream, options, "MessageIds");
            var messagesTask = ReadMessagesAsync(consumer, events.Count);

            await eventBus.PublishManyFromOutboxAsync(events, new OutboxConfig("OutboxBatch"));

            var messages = await messagesTask;
            messages.Count.ShouldBe(events.Count);
            foreach (var outgoingEvent in events)
            {
                var message = messages.Single(message =>
                    message.Subject == $"{options.SubjectPrefix}.{outgoingEvent.EventName}");

                message.Data.ShouldBe(outgoingEvent.EventData);
                GetHeader(message, "Nats-Msg-Id").ShouldBe(outgoingEvent.Id.ToString());
            }
        }
        finally
        {
            eventBus.Dispose();
            await DeleteStreamAsync(jetStream, options.StreamName);
        }
    }

    [NatsFact]
    public async Task PublishManyFromOutbox_Should_Emit_One_Outbox_Sent_Notification_Per_Event()
    {
        var options = CreateOptions("OutboxBatch.Notifications");
        var eventBus = CreateEventBus(options);
        var events = CreateEvents("OutboxBatch.Notifications", 3);
        var notifications = new ConcurrentQueue<DistributedEventSent>();
        var localEventBus = GetRequiredService<ILocalEventBus>();
        using var subscription = localEventBus.Subscribe<DistributedEventSent>(notification =>
        {
            if (events.Any(outgoingEvent => outgoingEvent.EventName == notification.EventName))
            {
                notifications.Enqueue(notification);
            }

            return Task.CompletedTask;
        });

        try
        {
            await eventBus.InitializeAsync();
            await eventBus.PublishManyFromOutboxAsync(events, new OutboxConfig("OutboxBatch"));

            notifications.Count.ShouldBe(events.Count);
            foreach (var outgoingEvent in events)
            {
                var notification = notifications.Single(item => item.EventName == outgoingEvent.EventName);
                notification.Source.ShouldBe(DistributedEventSource.Outbox);
                notification.EventData.ShouldBe(outgoingEvent.EventData);
            }
        }
        finally
        {
            eventBus.Dispose();
            var jetStream = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
            await DeleteStreamAsync(jetStream, options.StreamName);
        }
    }

    [NatsFact]
    public async Task PublishManyFromOutbox_Should_Stop_On_Partial_Failure()
    {
        var options = CreateOptions("OutboxBatch.PartialFailure");
        var eventBus = CreateFaultInjectingEventBus(options);
        var jetStream = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var events = CreateEvents("OutboxBatch.PartialFailure", 3);

        try
        {
            await eventBus.InitializeAsync();
            var consumer = await CreateConsumerAsync(jetStream, options, "PartialFailure");
            var firstMessageTask = ReadMessagesAsync(consumer, 1);
            eventBus.FailOnId = events[1].Id;

            var exception = await Record.ExceptionAsync(() =>
                eventBus.PublishManyFromOutboxAsync(events, new OutboxConfig("OutboxBatch")));

            exception.ShouldBeOfType<InvalidOperationException>();
            eventBus.AttemptedIds.ShouldBe([events[0].Id, events[1].Id]);
            var firstMessage = (await firstMessageTask).Single();
            GetHeader(firstMessage, "Nats-Msg-Id").ShouldBe(events[0].Id.ToString());
        }
        finally
        {
            eventBus.Dispose();
            await DeleteStreamAsync(jetStream, options.StreamName);
        }
    }

    [NatsFact]
    public async Task Retry_After_Partial_Batch_Failure_Should_Not_Create_Duplicate_Business_Delivery()
    {
        var options = CreateOptions("OutboxBatch.Retry");
        var eventBus = CreateFaultInjectingEventBus(options);
        var jetStream = await GetRequiredService<IJetStreamContextAccessor>().GetContextAsync();
        var events = CreateEvents("OutboxBatch.Retry", 3);

        try
        {
            await jetStream.CreateStreamAsync(new StreamConfig(
                options.StreamName,
                [$"{options.SubjectPrefix}.>"])
            {
                Retention = options.Retention,
                NumReplicas = options.ReplicaCount,
                DuplicateWindow = TimeSpan.FromMinutes(2)
            });
            await eventBus.InitializeAsync();
            var consumer = await CreateConsumerAsync(jetStream, options, "Retry");
            var messagesTask = ReadMessagesAsync(consumer, events.Count);
            eventBus.FailOnId = events[1].Id;

            await Record.ExceptionAsync(() =>
                eventBus.PublishManyFromOutboxAsync(events, new OutboxConfig("OutboxBatch")));

            eventBus.FailOnId = null;
            await eventBus.PublishManyFromOutboxAsync(events, new OutboxConfig("OutboxBatch"));

            var messages = await messagesTask;
            messages.Count.ShouldBe(events.Count);
            messages.Select(message => GetHeader(message, "Nats-Msg-Id"))
                .Distinct(StringComparer.Ordinal)
                .Count()
                .ShouldBe(events.Count);
            foreach (var outgoingEvent in events)
            {
                messages.Count(message =>
                    message.Subject == $"{options.SubjectPrefix}.{outgoingEvent.EventName}")
                    .ShouldBe(1);
            }
        }
        finally
        {
            eventBus.Dispose();
            await DeleteStreamAsync(jetStream, options.StreamName);
        }
    }

    private NatsDistributedEventBus CreateEventBus(NatsDistributedEventBusOptions options)
    {
        return ActivatorUtilities.CreateInstance<NatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(options));
    }

    private FaultInjectingNatsDistributedEventBus CreateFaultInjectingEventBus(
        NatsDistributedEventBusOptions options)
    {
        return ActivatorUtilities.CreateInstance<FaultInjectingNatsDistributedEventBus>(
            ServiceProvider,
            Options.Create(options));
    }

    private static NatsDistributedEventBusOptions CreateOptions(string name)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new NatsDistributedEventBusOptions
        {
            StreamName = $"{name.Replace('.', '_')}_{suffix}",
            SubjectPrefix = $"{suffix}.TrueParser.OutboxBatch.Events",
            ClientName = $"OutboxBatch_{suffix}"
        };
    }

    private static List<OutgoingEventInfo> CreateEvents(string name, int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new OutgoingEventInfo(
                Guid.NewGuid(),
                $"{name}.{index}",
                JsonSerializer.SerializeToUtf8Bytes(new { Index = index }),
                DateTime.UtcNow))
            .ToList();
    }

    private static async Task<INatsJSConsumer> CreateConsumerAsync(
        INatsJSContext jetStream,
        NatsDistributedEventBusOptions options,
        string suffix)
    {
        return await jetStream.CreateOrUpdateConsumerAsync(
            options.StreamName,
            new ConsumerConfig($"OutboxBatch_{suffix}_{Guid.NewGuid():N}")
            {
                FilterSubject = $"{options.SubjectPrefix}.>",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.New
            });
    }

    private static async Task<List<INatsJSMsg<byte[]>>> ReadMessagesAsync(
        INatsJSConsumer consumer,
        int count)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var messages = new List<INatsJSMsg<byte[]>>();
        await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: cancellation.Token))
        {
            messages.Add(message);
            await message.AckAsync();
            if (messages.Count == count)
            {
                break;
            }
        }

        return messages;
    }

    private static string? GetHeader(INatsJSMsg<byte[]> message, string name)
    {
        return message.Headers?.TryGetValue(name, out var values) == true
            ? values.FirstOrDefault()?.ToString()
            : null;
    }

    private static async Task DeleteStreamAsync(INatsJSContext jetStream, string streamName)
    {
        try
        {
            await jetStream.DeleteStreamAsync(streamName);
        }
        catch (NatsJSApiException ex) when (ex.Error.Code == 404)
        {
        }
    }
}
