namespace Natter.Chat.DingTalk;

/// <summary>Liveness verdict for a DingTalk Stream websocket (S2-A4).</summary>
public enum DingTalkStreamLivenessState
{
    /// <summary>No frame has been observed yet; nothing can be judged.</summary>
    Unknown,

    /// <summary>Recent inbound traffic (or an active socket): the connection looks alive.</summary>
    Healthy,

    /// <summary>No inbound frame inside the idle window: the TCP connection is probably half-open.</summary>
    Idle,

    /// <summary>An outbound ack went unanswered past the pong window: the peer stopped reading us.</summary>
    PongMissing,
}

/// <summary>
/// Pure half-open detector for the Stream transport. The old code only relied on
/// <c>ClientWebSocket.KeepAliveInterval</c> and never judged the socket, so a half-open
/// connection stayed <c>IsConnected=true</c> forever. This type owns the judgement so it can be
/// tested without a real websocket.
/// </summary>
public sealed class DingTalkStreamLiveness
{
    private readonly int _idleTimeoutMs;
    private readonly int _pongTimeoutMs;
    private DateTimeOffset? _lastInboundAt;
    private DateTimeOffset? _lastSendAt;

    public DingTalkStreamLiveness(int idleTimeoutMs, int pongTimeoutMs)
    {
        _idleTimeoutMs = Math.Max(10, idleTimeoutMs);
        _pongTimeoutMs = Math.Max(10, pongTimeoutMs);
    }

    public DateTimeOffset? LastInboundAt => _lastInboundAt;

    public DateTimeOffset? LastSendAt => _lastSendAt;

    /// <summary>
    /// How long a single receive may block before the state is re-judged. Derived from the
    /// shorter of the two windows so neither check can be skipped.
    /// </summary>
    public TimeSpan ReceiveWindow => TimeSpan.FromMilliseconds(Math.Min(_idleTimeoutMs, _pongTimeoutMs));

    /// <summary>A new socket just opened: treat it as proof of life.</summary>
    public void Reset(DateTimeOffset now)
    {
        _lastInboundAt = now;
        _lastSendAt = null;
    }

    public void RecordInbound(DateTimeOffset now) => _lastInboundAt = now;

    /// <summary>An outbound ack/frame was written; from now on the peer owes us a response.</summary>
    public void RecordSend(DateTimeOffset now) => _lastSendAt = now;

    public DingTalkStreamLivenessState Evaluate(DateTimeOffset now)
    {
        if (_lastInboundAt is not { } lastInbound)
            return DingTalkStreamLivenessState.Unknown;

        var sinceInbound = now - lastInbound;
        if (sinceInbound.TotalMilliseconds >= _idleTimeoutMs)
            return DingTalkStreamLivenessState.Idle;

        if (_lastSendAt is { } lastSend
            && now - lastSend >= TimeSpan.FromMilliseconds(_pongTimeoutMs)
            && sinceInbound >= TimeSpan.FromMilliseconds(_pongTimeoutMs))
        {
            return DingTalkStreamLivenessState.PongMissing;
        }

        return DingTalkStreamLivenessState.Healthy;
    }
}
