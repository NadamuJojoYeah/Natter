using Natter.Abstractions.Ports;

namespace Natter.Infrastructure.Adapters;

/// <summary>Default clock adapter. Replace it in tests or in a plugin via the IClock port.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
