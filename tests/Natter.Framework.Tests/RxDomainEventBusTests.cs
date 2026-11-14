using Microsoft.Extensions.Logging.Abstractions;
using Natter.Abstractions.Events;
using Natter.Domain.Demo;
using Natter.Infrastructure.Events;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class RxDomainEventBusTests
{
    [Fact]
    public async Task Publish_fans_out_envelopes_with_sequence_numbers()
    {
        var bus = new RxDomainEventBus(NullLogger<RxDomainEventBus>.Instance);
        var received = new List<DomainEventEnvelope>();
        using var subscription = bus.Subscribe(new DelegateObserver<DomainEventEnvelope>(received.Add));

        await bus.PublishAsync(new CounterCreated(Guid.NewGuid(), "demo", 0));
        await bus.PublishAsync(new CounterIncremented(Guid.NewGuid(), 1, 1));

        Assert.Equal(2, received.Count);
        Assert.Equal(1, received[0].Sequence);
        Assert.Equal(2, received[1].Sequence);
        Assert.Equal(2, bus.PublishedCount);
        Assert.Equal(1, bus.SubscriberCount);

        subscription.Dispose();
        Assert.Equal(0, bus.SubscriberCount);
        bus.Dispose();
    }

    [Fact]
    public async Task Type_filter_projects_concrete_events()
    {
        var bus = new RxDomainEventBus(NullLogger<RxDomainEventBus>.Instance);
        var increments = new List<CounterIncremented>();
        using var subscription = bus.OfTypeEvent<CounterIncremented>().Subscribe(
            new DelegateObserver<CounterIncremented>(increments.Add));

        await bus.PublishAsync(new CounterCreated(Guid.NewGuid(), "demo", 0));
        await bus.PublishAsync(new CounterIncremented(Guid.NewGuid(), 1, 1));

        Assert.Single(increments);
        Assert.Equal(1, increments[0].Value);
        bus.Dispose();
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_break_the_stream()
    {
        var bus = new RxDomainEventBus(NullLogger<RxDomainEventBus>.Instance);
        var observed = 0;

        using var broken = bus.Subscribe(new DelegateObserver<DomainEventEnvelope>(_ => throw new InvalidOperationException("boom")));
        using var healthy = bus.Subscribe(new DelegateObserver<DomainEventEnvelope>(_ => observed++));

        await bus.PublishAsync(new CounterIncremented(Guid.NewGuid(), 1, 1));

        Assert.Equal(1, observed);
        bus.Dispose();
    }

    [Fact]
    public async Task Dispatcher_drains_aggregate_events_once()
    {
        var bus = new RxDomainEventBus(NullLogger<RxDomainEventBus>.Instance);
        var dispatcher = new DomainEventDispatcher(bus, NullLogger<DomainEventDispatcher>.Instance);
        var counter = Counter.Create("demo");
        counter.Increment(1);

        var count = await dispatcher.DispatchAsync(counter);

        Assert.Equal(2, count);
        Assert.Empty(counter.DomainEvents);
        Assert.Equal(2, bus.PublishedCount);

        Assert.Equal(0, await dispatcher.DispatchAsync(counter));
        bus.Dispose();
    }

    private sealed class DelegateObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public DelegateObserver(Action<T> onNext) => _onNext = onNext;

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => _onNext(value);
    }
}
