namespace Natter.Chat;

/// <summary>
/// One protocol adaptor bound to one bot account. The adaptor owns platform authentication
/// and wire details; NLMP never carries tokens or raw credentials.
/// </summary>
public interface IChatUpstream : IChatEventSource
{
    Source Source { get; }

    bool IsConnected { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Capabilities for the source, optionally refined per conversation.</summary>
    ValueTask<Capabilities> GetCapabilitiesAsync(Conversation? conversation = null, CancellationToken cancellationToken = default);

    /// <summary>Executes one command and returns an ack. Adapters apply idempotency internally.</summary>
    ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Registry of connected upstreams keyed by structured <see cref="Source"/>.</summary>
public interface IChatUpstreamRegistry
{
    void Register(IChatUpstream upstream);

    bool Unregister(Source source);

    bool TryGet(Source source, out IChatUpstream? upstream);

    IReadOnlyList<IChatUpstream> Upstreams { get; }
}

/// <summary>Command path used by feature plugins and the orchestrator.</summary>
public interface IChatCommandDispatcher
{
    ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Consumes one NLMP event. Implementations must be idempotent because upstream events can
/// be replayed after a reconnect.
/// </summary>
public interface IChatMessageHandler
{
    ValueTask HandleAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default);
}
