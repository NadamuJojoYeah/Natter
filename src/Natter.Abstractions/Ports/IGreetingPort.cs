namespace Natter.Abstractions.Ports;

/// <summary>
/// Example driven port. The core adapter is registered by the framework, a plugin
/// can replace it without touching application code.
/// </summary>
public interface IGreetingPort
{
    ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken = default);
}
