using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Volo.Abp;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.Testing;
using Volo.Abp.Uow;
using TrueParser.Abp.Nats;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public class AbpEventBoxEndToEndTests : AbpIntegratedTest<TrueParserAbpEventBoxTestModule>, IAsyncLifetime
{
    private readonly NatsDistributedEventBus _eventBus;

    public AbpEventBoxEndToEndTests()
    {
        _eventBus = GetRequiredService<NatsDistributedEventBus>();
    }

    protected override void SetAbpApplicationCreationOptions(AbpApplicationCreationOptions options)
    {
        options.UseAutofac();
    }

    public async Task InitializeAsync()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TestEventBoxDbContext>();
        await dbContext.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [NatsFact]
    public async Task Publishing_Inside_Committed_UoW_Should_Flow_Through_ABP_Outbox_To_JetStream()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Outbox.Committed.{Guid.NewGuid():N}";
        var handlerInvocations = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                Interlocked.Increment(ref handlerInvocations);
                received.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();

        using var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
            requiresNew: true,
            isTransactional: true);

        await _eventBus.PublishAsync(
            new TestEventData { Message = eventName },
            onUnitOfWorkComplete: true,
            useOutbox: true);

        handlerInvocations.ShouldBe(0);
        (await QueryDatabaseAsync(db => db.OutgoingEvents
            .CountAsync(record => record.EventName == "TestEvent"))).ShouldBe(0);

        await uow.CompleteAsync();

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        handlerInvocations.ShouldBe(1);

        await WaitUntilAsync(async () => !await QueryDatabaseAsync(db => db.OutgoingEvents
            .AnyAsync(record => record.EventName == "TestEvent")));
    }

    [NatsFact]
    public async Task Rolled_Back_UoW_Should_Not_Publish_Outbox_Event()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Outbox.RolledBack.{Guid.NewGuid():N}";
        var handlerInvocations = 0;

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                Interlocked.Increment(ref handlerInvocations);
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();

        using (var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
                   requiresNew: true,
                   isTransactional: true))
        {
            await _eventBus.PublishAsync(
                new TestEventData { Message = eventName },
                onUnitOfWorkComplete: true,
                useOutbox: true);
        }

        await Task.Delay(500);

        handlerInvocations.ShouldBe(0);
        (await QueryDatabaseAsync(db => db.OutgoingEvents
            .AnyAsync(record => record.EventName == "TestEvent"))).ShouldBeFalse();
    }

    [NatsFact]
    public async Task Outbox_Worker_Should_Delete_Record_Only_After_Successful_NATS_Publish()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Outbox.Recovery.{Guid.NewGuid():N}";
        var handlerInvocations = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var natsOptions = GetRequiredService<IOptions<AbpNatsOptions>>().Value;
        var connectionPool = GetRequiredService<INatsConnectionPool>();
        var originalUrl = natsOptions.Connections;

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                Interlocked.Increment(ref handlerInvocations);
                received.TrySetResult();
            }

            return Task.CompletedTask;
        });

        try
        {
            await _eventBus.InitializeAsync();

            natsOptions.Connections = "nats://127.0.0.1:1";
            await ((NatsConnectionPool)connectionPool).DisposeAsync();

            using (var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
                       requiresNew: true,
                       isTransactional: true))
            {
                await _eventBus.PublishAsync(
                    new TestEventData { Message = eventName },
                    onUnitOfWorkComplete: true,
                    useOutbox: true);

                await uow.CompleteAsync();
            }

            await WaitUntilAsync(async () => await QueryDatabaseAsync(db => db.OutgoingEvents
                .AnyAsync(record => record.EventName == "TestEvent")));
            await Task.Delay(500);

            handlerInvocations.ShouldBe(0);
            (await QueryDatabaseAsync(db => db.OutgoingEvents
                .AnyAsync(record => record.EventName == "TestEvent"))).ShouldBeTrue();

            natsOptions.Connections = originalUrl;
            await ((NatsConnectionPool)connectionPool).DisposeAsync();

            await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handlerInvocations.ShouldBe(1);
            await WaitUntilAsync(async () => !await QueryDatabaseAsync(db => db.OutgoingEvents
                .AnyAsync(record => record.EventName == "TestEvent")));
        }
        finally
        {
            natsOptions.Connections = originalUrl;
            await ((NatsConnectionPool)connectionPool).DisposeAsync();
        }
    }

    [NatsFact]
    public async Task Incoming_NATS_Event_Should_Be_Processed_By_ABP_Inbox_Background_Processor()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Inbox.Processed.{Guid.NewGuid():N}";
        var handlerInvocations = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inboxNotification = new TaskCompletionSource<DistributedEventReceived>(TaskCreationOptions.RunContinuationsAsynchronously);
        var localEventBus = GetRequiredService<ILocalEventBus>();

        using var notificationSubscription = localEventBus.Subscribe<DistributedEventReceived>(notification =>
        {
            if (notification.EventName == "TestEvent" && notification.Source == DistributedEventSource.Inbox)
            {
                inboxNotification.TrySetResult(notification);
            }

            return Task.CompletedTask;
        });

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                Interlocked.Increment(ref handlerInvocations);
                received.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();

        await _eventBus.PublishAsync(
            new TestEventData { Message = eventName },
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var notification = await inboxNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));

        handlerInvocations.ShouldBe(1);
        notification.Source.ShouldBe(DistributedEventSource.Inbox);
        notification.EventData.ShouldBeOfType<TestEventData>().Message.ShouldBe(eventName);

        await WaitUntilAsync(async () => await QueryDatabaseAsync(db => db.IncomingEvents
            .AnyAsync(record => record.EventName == "TestEvent" && record.Status == IncomingEventStatus.Processed)));
        (await QueryDatabaseAsync(db => db.IncomingEvents
            .CountAsync(record => record.EventName == "TestEvent" && record.Status == IncomingEventStatus.Processed))).ShouldBe(1);
    }

    [NatsFact]
    public async Task Failing_Inbox_Handler_Should_Not_Be_Marked_Processed()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Inbox.Failing.{Guid.NewGuid():N}";
        var attempts = 0;
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                Interlocked.Increment(ref attempts);
                attempted.TrySetResult();
                throw new InvalidOperationException("coverage handler failure");
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();

        await _eventBus.PublishAsync(
            new TestEventData { Message = eventName },
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(250);

        attempts.ShouldBeGreaterThanOrEqualTo(1);
        (await QueryDatabaseAsync(db => db.IncomingEvents
            .AnyAsync(record => record.EventName == "TestEvent" && record.Status == IncomingEventStatus.Processed)))
            .ShouldBeFalse();
    }

    [NatsFact]
    public async Task Successful_Inbox_Handler_Should_Be_Executed_Exactly_Once()
    {
        await ResetEventBoxesAsync();
        var eventName = $"Coverage.Inbox.Duplicate.{Guid.NewGuid():N}";
        var messageId = Guid.NewGuid();
        var handlerInvocations = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == eventName)
            {
                if (Interlocked.Increment(ref handlerInvocations) == 1)
                {
                    received.TrySetResult();
                }
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();

        var outgoingEvent = new OutgoingEventInfo(
            messageId,
            "TestEvent",
            GetRequiredService<INatsEventSerializer>().Serialize(new TestEventData { Message = eventName }),
            DateTime.UtcNow);

        await _eventBus.PublishFromOutboxAsync(outgoingEvent, new OutboxConfig($"Coverage_{Guid.NewGuid():N}"));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await _eventBus.PublishFromOutboxAsync(outgoingEvent, new OutboxConfig($"Coverage_{Guid.NewGuid():N}"));
        await Task.Delay(750);

        handlerInvocations.ShouldBe(1);
        // ABP removes successfully processed Inbox rows after handling; the
        // business assertion is the exactly-once handler invocation above.
    }

    private async Task<T> QueryDatabaseAsync<T>(Func<TestEventBoxDbContext, Task<T>> query)
    {
        using var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
            requiresNew: true,
            isTransactional: false);
        var dbContext = await GetRequiredService<IDbContextProvider<TestEventBoxDbContext>>()
            .GetDbContextAsync();
        var result = await query(dbContext);
        await uow.CompleteAsync();
        return result;
    }

    private async Task ResetEventBoxesAsync()
    {
        await QueryDatabaseAsync(async db =>
        {
            await db.IncomingEvents.ExecuteDeleteAsync();
            await db.OutgoingEvents.ExecuteDeleteAsync();
            return true;
        });
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
