namespace Natter.Chat;

/// <summary>Optional richer health view exposed by upstreams; health falls back to IsConnected.</summary>
public sealed record UpstreamHealth(
    string SourceKey,
    bool Connected,
    DateTimeOffset? LastEventAt,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectAt,
    int ReconnectCount,
    string? LastError,
    int SendFailures = 0,
    string? LastSendError = null);

public interface IChatUpstreamHealth
{
    UpstreamHealth Health { get; }
}
