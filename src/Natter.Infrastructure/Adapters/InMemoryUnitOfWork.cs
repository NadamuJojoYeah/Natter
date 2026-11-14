using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Adapters;

/// <summary>Minimal unit-of-work adapter used until a real transaction adapter is plugged in.</summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private long _commitCount;

    public long CommitCount => Interlocked.Read(ref _commitCount);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _commitCount);
        return Task.FromResult(1);
    }
}
