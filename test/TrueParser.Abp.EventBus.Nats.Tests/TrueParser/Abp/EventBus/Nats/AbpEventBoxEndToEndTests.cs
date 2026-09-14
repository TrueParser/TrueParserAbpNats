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
using Volo.Abp.MultiTenancy;
using Volo.Abp.Testing;
using Volo.Abp.Tracing;
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

    [NatsFact]
    public async Task Representative_Eto_Should_Preserve_Business_Fields_Through_Outbox_And_Inbox()
    {
        await ResetEventBoxesAsync();
        var expected = new AcceptancePayloadEto
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            State = AcceptanceState.Active,
            Amount = 1234.56m,
            Enabled = true,
            Description = string.Empty,
            Tags = ["priority", "migration"],
            Details = new AcceptanceDetails { Code = "CP-42", Count = 7 }
        };
        var received = new TaskCompletionSource<AcceptancePayloadEto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedFromInbox = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localEventBus = GetRequiredService<ILocalEventBus>();

        using var notificationSubscription = localEventBus.Subscribe<DistributedEventReceived>(notification =>
        {
            if (notification.EventName == "Acceptance.Payload" && notification.Source == DistributedEventSource.Inbox)
            {
                receivedFromInbox.TrySetResult();
            }

            return Task.CompletedTask;
        });
        using var subscription = _eventBus.Subscribe<AcceptancePayloadEto>(data =>
        {
            received.TrySetResult(data);
            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();
        using var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
            requiresNew: true,
            isTransactional: true);

        await _eventBus.PublishAsync(expected, onUnitOfWorkComplete: true, useOutbox: true);
        await uow.CompleteAsync();

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await receivedFromInbox.Task.WaitAsync(TimeSpan.FromSeconds(5));
        actual.ShouldBeEquivalentTo(expected);
    }

    [NatsFact]
    public async Task Tenant_Scoped_Eto_Should_Preserve_Tenant_Through_Real_ABP_Inbox()
    {
        await ResetEventBoxesAsync();
        var tenantId = Guid.NewGuid();
        var receivedTenant = new TaskCompletionSource<Guid?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TenantAcceptanceEto>(data =>
        {
            receivedTenant.TrySetResult(GetRequiredService<ICurrentTenant>().Id);
            received.TrySetResult();
            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();
        using (GetRequiredService<ICurrentTenant>().Change(tenantId))
        using (var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
                   requiresNew: true,
                   isTransactional: true))
        {
            await _eventBus.PublishAsync(
                new TenantAcceptanceEto { TenantId = tenantId, Value = "tenant-a" },
                onUnitOfWorkComplete: true,
                useOutbox: true);
            await uow.CompleteAsync();
        }

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        (await receivedTenant.Task).ShouldBe(tenantId);
    }

    [NatsFact]
    public async Task Host_Eto_Should_Execute_With_No_Tenant_Through_Real_ABP_Inbox()
    {
        await ResetEventBoxesAsync();
        var receivedTenant = new TaskCompletionSource<Guid?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TenantAcceptanceEto>(data =>
        {
            receivedTenant.TrySetResult(GetRequiredService<ICurrentTenant>().Id);
            received.TrySetResult();
            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();
        await _eventBus.PublishAsync(
            new TenantAcceptanceEto { TenantId = null, Value = "host" },
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        (await receivedTenant.Task).ShouldBeNull();
    }

    [NatsFact]
    public async Task Domain_Event_Bridge_Should_Publish_An_Eto_Through_ABP_Outbox()
    {
        await ResetEventBoxesAsync();
        var value = $"bridge-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<BridgeAcceptanceEto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var distributedSubscription = _eventBus.Subscribe<BridgeAcceptanceEto>(data =>
        {
            received.TrySetResult(data);
            return Task.CompletedTask;
        });
        using var domainSubscription = GetRequiredService<ILocalEventBus>()
            .Subscribe<AcceptanceDomainEvent>(domainEvent =>
            {
                return _eventBus.PublishAsync(
                    new BridgeAcceptanceEto { Value = domainEvent.Value },
                    onUnitOfWorkComplete: true,
                    useOutbox: true);
            });

        await _eventBus.InitializeAsync();
        using var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
            requiresNew: true,
            isTransactional: true);

        await GetRequiredService<ILocalEventBus>().PublishAsync(new AcceptanceDomainEvent { Value = value });
        await uow.CompleteAsync();

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(15))).Value.ShouldBe(value);
    }

    [NatsFact]
    public async Task Outbox_Correlation_Should_Reach_The_ABP_Inbox_Handler()
    {
        await ResetEventBoxesAsync();
        const string correlationId = "independent-acceptance-correlation";
        var observedCorrelation = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var subscription = _eventBus.Subscribe<TestEventData>(data =>
        {
            if (data.Message == correlationId)
            {
                observedCorrelation.TrySetResult(GetRequiredService<ICorrelationIdProvider>().Get());
                received.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();
        using (GetRequiredService<ICorrelationIdProvider>().Change(correlationId))
        using (var uow = GetRequiredService<IUnitOfWorkManager>().Begin(
                   requiresNew: true,
                   isTransactional: true))
        {
            await _eventBus.PublishAsync(
                new TestEventData { Message = correlationId },
                onUnitOfWorkComplete: true,
                useOutbox: true);
            await uow.CompleteAsync();
        }

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        (await observedCorrelation.Task).ShouldBe(correlationId);
    }

    [NatsFact]
    public async Task Similar_Event_Names_Should_Not_Invoke_An_Unrelated_Inbox_Handler()
    {
        await ResetEventBoxesAsync();
        var changedInvocations = 0;
        var retiredInvocations = 0;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var changedSubscription = _eventBus.Subscribe<AcceptancePlanChangedEto>(data =>
        {
            Interlocked.Increment(ref changedInvocations);
            changed.TrySetResult();
            return Task.CompletedTask;
        });
        using var retiredSubscription = _eventBus.Subscribe<AcceptancePlanRetiredEto>(data =>
        {
            Interlocked.Increment(ref retiredInvocations);
            return Task.CompletedTask;
        });

        await _eventBus.InitializeAsync();
        await _eventBus.PublishAsync(
            new AcceptancePlanChangedEto { PlanId = Guid.NewGuid(), Name = "starter" },
            onUnitOfWorkComplete: false,
            useOutbox: false);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500);
        Volatile.Read(ref changedInvocations).ShouldBe(1);
        Volatile.Read(ref retiredInvocations).ShouldBe(0);
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

[EventName("Acceptance.Payload")]
public sealed class AcceptancePayloadEto : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public AcceptanceState State { get; set; }
    public decimal Amount { get; set; }
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = [];
    public AcceptanceDetails Details { get; set; } = new();
}

public enum AcceptanceState
{
    Pending,
    Active
}

public sealed class AcceptanceDetails
{
    public string? Code { get; set; }
    public int Count { get; set; }
}

[EventName("Acceptance.Tenant")]
public sealed class TenantAcceptanceEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public string? Value { get; set; }
}

public sealed class AcceptanceDomainEvent
{
    public string? Value { get; set; }
}

[EventName("Acceptance.Bridge")]
public sealed class BridgeAcceptanceEto
{
    public string? Value { get; set; }
}

[EventName("Acceptance.PlanChanged")]
public sealed class AcceptancePlanChangedEto
{
    public Guid PlanId { get; set; }
    public string? Name { get; set; }
}

[EventName("Acceptance.PlanRetired")]
public sealed class AcceptancePlanRetiredEto
{
    public Guid PlanId { get; set; }
}
