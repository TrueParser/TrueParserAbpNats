using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using NSubstitute;
using Shouldly;
using TrueParser.Abp.Nats;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Tracing;
using Volo.Abp.Uow;
using Volo.Abp.Timing;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsEventBusLifecycle_Tests
{
    [Fact]
    public async Task JetStream_api_error_after_start_should_retry_consumer_loop()
    {
        using var cancellation = new CancellationTokenSource();
        var thirdLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = Substitute.For<INatsJSConsumer>();
        consumer.Info.Returns(new ConsumerInfo
        {
            StreamName = "LifecycleStream",
            Name = "LifecycleConsumer",
            Ts = DateTimeOffset.UtcNow,
            Config = new ConsumerConfig("LifecycleConsumer")
            {
                FilterSubject = "Lifecycle.ApiRecovery",
                AckPolicy = ConsumerConfigAckPolicy.Explicit
            },
            Created = DateTimeOffset.UtcNow,
            Delivered = new SequenceInfo(),
            AckFloor = new SequenceInfo()
        });
        consumer.ConsumeAsync<byte[]>(cancellationToken: Arg.Any<CancellationToken>())
            .Returns(TestAsyncEnumerable.EmptyMessages());

        var jetStream = Substitute.For<INatsJSContext>();
        var lookupCount = 0;
        jetStream.GetConsumerAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var currentLookup = Interlocked.Increment(ref lookupCount);
                if (currentLookup == 2)
                {
                    throw new NatsJSApiException(new ApiError { Code = 503, Description = "temporary API error" });
                }

                if (currentLookup == 3)
                {
                    thirdLookup.TrySetResult();
                    cancellation.Cancel();
                }

                return ValueTask.FromResult(consumer);
            });

        var accessor = Substitute.For<IJetStreamContextAccessor>();
        accessor.GetContextAsync(Arg.Any<string?>())
            .Returns(ValueTask.FromResult(jetStream));

        using var eventBus = new ApiRecoveryNatsDistributedEventBus(accessor);
        var startupSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerTask = eventBus.RunConsumerAsync(cancellation.Token, startupSignal);

        try
        {
            await startupSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            startupSignal.Task.IsCompletedSuccessfully.ShouldBeTrue();
            var retryOrExit = await Task.WhenAny(thirdLookup.Task, consumerTask, Task.Delay(TimeSpan.FromSeconds(12)));
            retryOrExit.ShouldBe(thirdLookup.Task);
            await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
            lookupCount.ShouldBe(3);
        }
        finally
        {
            cancellation.Cancel();
            await consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Typed_and_dynamic_subscriptions_for_the_same_name_should_start_one_consumer()
    {
        using var eventBus = new TrackingNatsDistributedEventBus();
        var typedSubscription = eventBus.Subscribe<LifecycleEvent>(_ => Task.CompletedTask);
        var dynamicSubscription = eventBus.Subscribe("Lifecycle.Shared", new LifecycleDynamicHandler());

        await eventBus.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        eventBus.StartCount.ShouldBe(1);

        eventBus.ReleaseConsumers();
        typedSubscription.Dispose();
        dynamicSubscription.Dispose();
    }

    [Fact]
    public async Task Dispose_should_wait_for_an_active_consumer_to_finish()
    {
        var eventBus = new TrackingNatsDistributedEventBus();
        eventBus.Subscribe<LifecycleEvent>(_ => Task.CompletedTask);
        await eventBus.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = Task.Run(eventBus.Dispose);
        await Task.Delay(100);
        disposeTask.IsCompleted.ShouldBeFalse();

        eventBus.ReleaseConsumers();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Async_shutdown_should_wait_for_an_active_consumer_to_finish()
    {
        var eventBus = new TrackingNatsDistributedEventBus();
        eventBus.Subscribe<LifecycleEvent>(_ => Task.CompletedTask);
        await eventBus.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdownTask = eventBus.OnApplicationShutdownAsync(null!);
        await Task.Delay(100);
        shutdownTask.IsCompleted.ShouldBeFalse();

        eventBus.ReleaseConsumers();
        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5));
        eventBus.Dispose();
    }

    [Fact]
    public async Task Restart_after_unsubscribe_should_create_a_new_startup_signal()
    {
        using var eventBus = new TrackingNatsDistributedEventBus();
        var firstValue = 0;
        var firstSubscription = eventBus.Subscribe<LifecycleEvent>(eventData =>
        {
            GC.KeepAlive(firstValue);
            return Task.CompletedTask;
        });
        await eventBus.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var firstSignal = eventBus.LastStartupSignal!;

        firstSubscription.Dispose();
        eventBus.UnsubscribeAll(typeof(LifecycleEvent));
        eventBus.ReleaseConsumers();
        await Task.Delay(100);
        var secondValue = 0;
        var secondSubscription = eventBus.Subscribe<LifecycleEvent>(eventData =>
        {
            GC.KeepAlive(secondValue);
            return Task.CompletedTask;
        });
        await eventBus.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondSignal = eventBus.LastStartupSignal!;

        secondSignal.ShouldNotBeSameAs(firstSignal);
        secondSignal.Task.IsCompleted.ShouldBeTrue();

        eventBus.ReleaseConsumers();
        secondSubscription.Dispose();
    }

    [Fact]
    public async Task Immediate_resubscribe_after_unsubscribe_should_start_a_successor_consumer()
    {
        using var eventBus = new TrackingNatsDistributedEventBus();
        var firstSubscription = eventBus.Subscribe<LifecycleEvent>(_ => Task.CompletedTask);
        await eventBus.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        firstSubscription.Dispose();
        var secondSubscription = eventBus.Subscribe<LifecycleEvent>(_ => Task.CompletedTask);

        eventBus.ReleaseConsumers();
        await eventBus.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        eventBus.StartCount.ShouldBe(2);

        secondSubscription.Dispose();
    }

    [Fact]
    public async Task Concurrent_context_requests_should_share_the_cached_context()
    {
        var connection = new NatsConnection(NatsOpts.Default);
        var pool = new SynchronizedConnectionPool(connection);

        var accessor = new JetStreamContextAccessor(pool);
        var contexts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => accessor.GetContextAsync().AsTask()));

        contexts.All(context => ReferenceEquals(context, contexts[0])).ShouldBeTrue();
        await connection.DisposeAsync();
    }
}

[DisableConventionalRegistration]
internal sealed class ApiRecoveryNatsDistributedEventBus : NatsDistributedEventBus
{
    public ApiRecoveryNatsDistributedEventBus(IJetStreamContextAccessor accessor)
        : base(
            Options.Create(new NatsDistributedEventBusOptions
            {
                StreamName = "LifecycleStream",
                SubjectPrefix = "Lifecycle",
                ClientName = "LifecycleApiRecovery"
            }),
            accessor,
            Substitute.For<INatsEventSerializer>(),
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

    public Task RunConsumerAsync(CancellationToken cancellationToken, TaskCompletionSource startupSignal) =>
        base.SubscribeToSubjectAsync("ApiRecovery", cancellationToken, startupSignal);

    protected override Task EnsureStreamExistsAsync() => Task.CompletedTask;
}

internal static class TestAsyncEnumerable
{
    public static async IAsyncEnumerable<INatsJSMsg<byte[]>> EmptyMessages()
    {
        await Task.Yield();
        yield break;
    }
}

[EventName("Lifecycle.Shared")]
public sealed class LifecycleEvent;

public sealed class LifecycleDynamicHandler : IDistributedEventHandler<DynamicEventData>
{
    public Task HandleEventAsync(DynamicEventData eventData) => Task.CompletedTask;
}

[DisableConventionalRegistration]
internal sealed class TrackingNatsDistributedEventBus : NatsDistributedEventBus
{
    private readonly TaskCompletionSource _releaseConsumers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startCount;

    public TrackingNatsDistributedEventBus()
        : base(
            Options.Create(new NatsDistributedEventBusOptions()),
            Substitute.For<IJetStreamContextAccessor>(),
            Substitute.For<INatsEventSerializer>(),
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

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource? LastStartupSignal { get; private set; }

    public int StartCount => Volatile.Read(ref _startCount);

    public void ReleaseConsumers() => _releaseConsumers.TrySetResult();

    protected override async Task SubscribeToSubjectAsync(
        string eventName,
        CancellationToken consumerCancellationToken,
        TaskCompletionSource startupSignal)
    {
        LastStartupSignal = startupSignal;
        startupSignal.TrySetResult();
        if (Interlocked.Increment(ref _startCount) == 2)
        {
            SecondStarted.TrySetResult();
        }
        Started.TrySetResult();
        await _releaseConsumers.Task;
    }
}

internal sealed class SynchronizedConnectionPool : TrueParser.Abp.Nats.INatsConnectionPool
{
    private readonly INatsConnection _connection;
    private readonly TaskCompletionSource _allRequests = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _getCount;

    public SynchronizedConnectionPool(INatsConnection connection)
    {
        _connection = connection;
    }

    public int GetCount => Volatile.Read(ref _getCount);

    public ValueTask<INatsConnection> GetAsync(string? connectionName = null)
    {
        if (Interlocked.Increment(ref _getCount) == 32)
        {
            _allRequests.TrySetResult();
        }

        return GetConnectionAsync();
    }

    private async ValueTask<INatsConnection> GetConnectionAsync()
    {
        await _allRequests.Task;
        return _connection;
    }
}
