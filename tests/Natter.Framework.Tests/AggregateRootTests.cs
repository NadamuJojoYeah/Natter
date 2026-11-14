using Xunit;
using Natter.Domain.Demo;
using Natter.Abstractions.Ddd;

namespace Natter.Framework.Tests;

public sealed class AggregateRootTests
{
    [Fact]
    public void Counter_raises_domain_events_and_tracks_state()
    {
        var counter = Counter.Create("demo");

        counter.Increment(2);
        counter.Increment(3);

        Assert.Equal(5, counter.Value);
        Assert.Equal(3, counter.DomainEvents.Count);
        Assert.IsType<CounterCreated>(counter.DomainEvents.First());
        Assert.Equal(2, counter.DomainEvents.OfType<CounterIncremented>().Count());
    }

    [Fact]
    public void ClearDomainEvents_empties_the_buffer()
    {
        var counter = Counter.Create("demo");
        counter.Increment(1);

        counter.ClearDomainEvents();

        Assert.Empty(counter.DomainEvents);
    }

    [Fact]
    public void Entities_use_identity_equality()
    {
        var counter = Counter.Create("demo");
        var sameIdentity = Counter.Rehydrate(counter.Id, "renamed", 42);
        var other = Counter.Create("demo");

        Assert.Equal(counter, sameIdentity);
        Assert.NotEqual(counter, other);
        Assert.NotEqual(counter.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void Domain_guards_reject_invalid_input()
    {
        Assert.Throws<DomainException>(() => Counter.Create("  "));
        Assert.Throws<DomainException>(() => Counter.Create("ok").Increment(0));
    }

    [Fact]
    public void Value_objects_compare_by_components()
    {
        Assert.Equal(new Money(10, "EUR"), new Money(10, "EUR"));
        Assert.NotEqual(new Money(10, "EUR"), new Money(10, "USD"));
    }

    private sealed class Money : ValueObject
    {
        public Money(decimal amount, string currency)
        {
            Amount = amount;
            Currency = currency;
        }

        public decimal Amount { get; }

        public string Currency { get; }

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }
}
