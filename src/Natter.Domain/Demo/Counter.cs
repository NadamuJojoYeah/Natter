using Natter.Abstractions.Ddd;
using Natter.Abstractions.Events;

namespace Natter.Domain.Demo;

/// <summary>
/// Minimal demo aggregate used by the framework's `demo` command. It is intentionally
/// trivial: the point is the aggregate -> domain event -> Rx pipeline, not the business.
/// </summary>
public sealed class Counter : AggregateRoot<Guid>
{
    private Counter(Guid id, string name, int value)
        : base(id)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public int Value { get; private set; }

    public static Counter Create(string name)
    {
        Guard.NotNullOrWhiteSpace(name, nameof(name));
        var counter = new Counter(Guid.NewGuid(), name.Trim(), 0);
        counter.Raise(new CounterCreated(counter.Id, counter.Name, counter.Value));
        return counter;
    }

    /// <summary>Factory used by persistence adapters to rehydrate an aggregate.</summary>
    public static Counter Rehydrate(Guid id, string name, int value)
    {
        Guard.NotNull(name, nameof(name));
        Guard.NotNegative(value, nameof(value));
        return new Counter(id, name, value);
    }

    public void Increment(int amount)
    {
        Guard.Positive(amount, nameof(amount));
        Value += amount;
        Raise(new CounterIncremented(Id, Value, amount));
    }

    public void Reset()
    {
        if (Value == 0)
            return;

        var previous = Value;
        Value = 0;
        Raise(new CounterReset(Id, previous));
    }
}

public sealed record CounterCreated(Guid CounterId, string Name, int Value) : DomainEvent;

public sealed record CounterIncremented(Guid CounterId, int Value, int Amount) : DomainEvent;

public sealed record CounterReset(Guid CounterId, int PreviousValue) : DomainEvent;
