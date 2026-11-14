using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Adapters;

/// <summary>
/// Default implementation of the greeting driven port. A plugin can override this
/// binding (see Natter.Sample.Plugin.Greetings) without changing application code.
/// </summary>
public sealed class DefaultGreetingAdapter : IGreetingPort
{
    private readonly IClock _clock;

    public DefaultGreetingAdapter(IClock clock) => _clock = clock;

    public ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            $"Hello, {name}. [core greeting adapter @ {_clock.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]");
    }
}
