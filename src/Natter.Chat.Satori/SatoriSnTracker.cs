using System.Text.Json;

namespace Natter.Chat.Satori;

/// <summary>
/// Remembers the last processed Satori <c>sn</c> so a reconnect can resume and replay is
/// deduplicated. Persistence is deliberately left to the adapter host.
/// </summary>
public sealed class SatoriSnTracker
{
    private readonly Lock _gate = new();
    private long? _lastSn;

    public long? LastSn
    {
        get
        {
            lock (_gate)
                return _lastSn;
        }
    }

    /// <summary>Advances the tracker; returns false for a replayed or older sequence.</summary>
    public bool TryAdvance(long sn)
    {
        lock (_gate)
        {
            if (_lastSn is { } last && sn <= last)
                return false;

            _lastSn = sn;
            return true;
        }
    }

    public void Reset()
    {
        lock (_gate)
            _lastSn = null;
    }

    /// <summary>Builds the IDENTIFY payload; the token must never be logged.</summary>
    public string BuildIdentifyPayload(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var payload = new Dictionary<string, object?> { ["token"] = token };

        lock (_gate)
        {
            if (_lastSn is { } sn)
                payload["sn"] = sn;
        }

        return JsonSerializer.Serialize(payload, SatoriJson.Options);
    }
}
