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

    /// <summary>True when the frame is newer than the last committed sequence.</summary>
    public bool ShouldProcess(long sn)
    {
        lock (_gate)
            return _lastSn is not { } last || sn > last;
    }

    /// <summary>Commits a sequence after its event has been mapped successfully.</summary>
    public bool Commit(long sn)
    {
        lock (_gate)
        {
            if (_lastSn is { } last && sn <= last)
                return false;

            _lastSn = sn;
            return true;
        }
    }

    /// <summary>Advances the tracker atomically; kept for callers that map before calling it.</summary>
    public bool TryAdvance(long sn)
        => ShouldProcess(sn) && Commit(sn);

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
