namespace Natter.Abstractions.Ports;

/// <summary>Outbound port for time. Keeps the domain deterministic and testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
