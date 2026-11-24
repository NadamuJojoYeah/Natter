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

    /// <summary>
    /// Executes one command and returns an ack.
    /// <para>
    /// <b>Idempotency</b> is the adapter's job (the host may retry a chunk).
    /// </para>
    /// <para>
    /// <b>Chunking is a conditional obligation</b> (docs/CONTRIBUTION_OBLIGATIONS.md §3.1):
    /// </para>
    /// <list type="number">
    ///   <item><b>If the adapter declares <see cref="Capabilities.MaxMessageLength"/></b> it MUST split
    ///   outbound content at that bound (see <c>ChatTextChunker.Chunk</c>). Declaring a limit and
    ///   sending over it is how the Satori adapter shipped replies the platform rejected whole.</item>
    ///   <item><b>If it declares <see langword="null"/></b> it is waiving that obligation — which is
    ///   legitimate for an aggregation protocol whose real limit depends on the platform behind the
    ///   gateway. The waiver MUST be observable (a warning or a health field); a silent waiver is the
    ///   defect this contract exists to prevent.</item>
    /// </list>
    /// <para>There is no third state: an adapter that neither chunks nor records the waiver is
    /// incomplete, and a test enforces exactly that.</para>
    /// </summary>
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
