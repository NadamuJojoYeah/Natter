using Natter.Abstractions.Ports;

namespace Natter.Sample.Plugin.Greetings;

/// <summary>Alternative adapter for the IGreetingPort driven port.</summary>
public sealed class FancyGreetingAdapter : IGreetingPort
{
    private readonly IClock _clock;

    public FancyGreetingAdapter(IClock clock) => _clock = clock;

    public ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            $"Hey {name}, this greeting came from the greetings plugin adapter ({_clock.UtcNow:HH:mm:ss} UTC).");
    }
}
