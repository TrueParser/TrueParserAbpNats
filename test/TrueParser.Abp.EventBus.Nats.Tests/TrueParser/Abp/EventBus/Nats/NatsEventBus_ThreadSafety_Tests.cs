using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Volo.Abp.EventBus;
using Volo.Abp.EventBus.Distributed;
using Xunit;

namespace TrueParser.Abp.EventBus.Nats;

public class NatsEventBus_ThreadSafety_Tests : NatsEventBusTestBase
{
    private readonly IDistributedEventBus _distributedEventBus;

    public NatsEventBus_ThreadSafety_Tests()
    {
        _distributedEventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Concurrent_Subscribe_And_Unsubscribe_On_Typed_Event_Should_Not_Throw()
    {
        var eventName = $"ThreadSafety.Typed.{Guid.NewGuid():N}";
        var errors = new ConcurrentQueue<Exception>();
        var subscriptions = new ConcurrentBag<IDisposable>();

        await RunConcurrentAsync(32, async _ =>
        {
            try
            {
                var subscription = _distributedEventBus.Subscribe<ThreadSafetyTypedEvent>(data => Task.CompletedTask);
                subscriptions.Add(subscription);
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }

            await Task.CompletedTask;
        });

        errors.ShouldBeEmpty();

        await RunConcurrentAsync(32, async _ =>
        {
            try
            {
                _distributedEventBus.UnsubscribeAll(typeof(ThreadSafetyTypedEvent));
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }

            await Task.CompletedTask;
        });

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_Subscribe_And_Publish_On_Dynamic_Event_Should_Deliver_Without_Races()
    {
        var eventName = $"ThreadSafety.Dynamic.{Guid.NewGuid():N}";
        var received = new ConcurrentDictionary<int, byte>();
        var errors = new ConcurrentQueue<Exception>();
        var subscriptions = new ConcurrentBag<IDisposable>();
        var readySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await RunConcurrentAsync(24, async index =>
        {
            try
            {
                var subscription = _distributedEventBus.Subscribe(eventName, new ThreadSafetyDynamicHandler(eventData =>
                {
                    if (eventData.Data is not null)
                    {
                        received.TryAdd(index, 0);
                        if (received.Count >= 8)
                        {
                            readySignal.TrySetResult();
                        }
                    }
                }));

                subscriptions.Add(subscription);
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }

            await Task.CompletedTask;
        });

        errors.ShouldBeEmpty();

        await Task.Delay(1000);

        await RunConcurrentAsync(16, async i =>
        {
            try
            {
                await _distributedEventBus.PublishAsync(
                    typeof(DynamicEventData),
                    new DynamicEventData(eventName, new { Value = i }),
                    onUnitOfWorkComplete: false,
                    useOutbox: false);
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        await readySignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        errors.ShouldBeEmpty();
        received.Count.ShouldBeGreaterThanOrEqualTo(1);

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    [Fact]
    public async Task Concurrent_Dynamic_Handler_Mutation_While_Publishing_Should_Not_Throw()
    {
        var eventName = $"ThreadSafety.Mutation.{Guid.NewGuid():N}";
        var errors = new ConcurrentQueue<Exception>();
        var received = 0;
        using var initialSubscription = _distributedEventBus.Subscribe(eventName, new ThreadSafetyDynamicHandler(_ =>
        {
            Interlocked.Increment(ref received);
        }));

        await Task.Delay(1000);

        var tasks = Enumerable.Range(0, 24)
            .Select(workerId => Task.Run(async () =>
            {
                for (var i = 0; i < 20; i++)
                {
                    try
                    {
                        if (i % 2 == 0)
                        {
                            using var subscription = _distributedEventBus.Subscribe(eventName, new ThreadSafetyDynamicHandler(_ =>
                            {
                                Interlocked.Increment(ref received);
                            }));

                            await _distributedEventBus.PublishAsync(
                                typeof(DynamicEventData),
                                new DynamicEventData(eventName, new { Worker = workerId, Iteration = i }),
                                onUnitOfWorkComplete: false,
                                useOutbox: false);
                        }
                        else
                        {
                            await _distributedEventBus.PublishAsync(
                                typeof(DynamicEventData),
                                new DynamicEventData(eventName, new { Worker = workerId, Iteration = i }),
                                onUnitOfWorkComplete: false,
                                useOutbox: false);

                            _distributedEventBus.UnsubscribeAll(eventName);
                            _distributedEventBus.Subscribe(eventName, new ThreadSafetyDynamicHandler(_ =>
                            {
                                Interlocked.Increment(ref received);
                            })).Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }
            }));

        await Task.WhenAll(tasks);

        errors.ShouldBeEmpty();
        received.ShouldBeGreaterThan(0);
    }

    private static async Task RunConcurrentAsync(int count, Func<int, Task> action)
    {
        var tasks = Enumerable.Range(0, count).Select(action);
        await Task.WhenAll(tasks);
    }
}

[EventName("ThreadSafetyTypedEvent")]
public class ThreadSafetyTypedEvent
{
    public string? Value { get; set; }
}

public sealed class ThreadSafetyDynamicHandler : IDistributedEventHandler<DynamicEventData>
{
    private readonly Action<DynamicEventData> _onReceived;

    public ThreadSafetyDynamicHandler(Action<DynamicEventData> onReceived)
    {
        _onReceived = onReceived;
    }

    public Task HandleEventAsync(DynamicEventData eventData)
    {
        _onReceived(eventData);
        return Task.CompletedTask;
    }
}
