using Natter.Abstractions.Application;
using Natter.Abstractions.Events;
using Natter.Abstractions.Ports;
using Natter.Domain.Demo;

namespace Natter.Application.Demo;

/// <summary>Application command used by the CLI demo. Not a business feature set.</summary>
public sealed record IncrementCounterCommand(string Name, int Amount, int Times)
    : ICommand<CounterSnapshot>;

/// <summary>Read model returned to the caller.</summary>
public sealed record CounterSnapshot(Guid Id, string Name, int Value, int EventsRaised);

/// <summary>
/// Use case: create a counter, apply increments, persist through the repository port,
/// then dispatch the buffered domain events into the event stream.
/// </summary>
public sealed class IncrementCounterHandler : IRequestHandler<IncrementCounterCommand, CounterSnapshot>
{
    private readonly IRepository<Counter, Guid> _repository;
    private readonly IDomainEventDispatcher _domainEventDispatcher;
    private readonly IUnitOfWork _unitOfWork;

    public IncrementCounterHandler(
        IRepository<Counter, Guid> repository,
        IDomainEventDispatcher domainEventDispatcher,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _domainEventDispatcher = domainEventDispatcher;
        _unitOfWork = unitOfWork;
    }

    public async Task<CounterSnapshot> HandleAsync(
        IncrementCounterCommand request,
        CancellationToken cancellationToken = default)
    {
        var counter = Counter.Create(request.Name);

        for (var i = 0; i < Math.Max(1, request.Times); i++)
            counter.Increment(request.Amount);

        await _repository.AddAsync(counter, cancellationToken);
        var eventsRaised = await _domainEventDispatcher.DispatchAsync(counter, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new CounterSnapshot(counter.Id, counter.Name, counter.Value, eventsRaised);
    }
}
