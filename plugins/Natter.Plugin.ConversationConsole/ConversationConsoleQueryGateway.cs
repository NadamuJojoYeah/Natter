using Natter.Application.Chat;

namespace Natter.Plugin.ConversationConsole;

/// <summary>One conversation row rendered by the console.</summary>
public sealed record ConversationConsoleSession(
    string SessionId,
    string SourceKey,
    string Protocol,
    string Platform,
    string ConversationId,
    string? GuildId,
    string? ActorId,
    int MessageCount,
    DateTimeOffset FirstMessageAt,
    DateTimeOffset LastMessageAt);

/// <summary>
/// One message row. <paramref name="Truncated"/> marks a body that exceeded the console's display
/// bound, so the UI can say "truncated" instead of pretending the text is complete.
/// </summary>
public sealed record ConversationConsoleMessage(
    long MessageId,
    string Role,
    string Text,
    DateTimeOffset CreatedAt,
    bool Truncated);

public sealed record ConversationConsolePage(
    IReadOnlyList<ConversationConsoleSession> Items,
    string? NextCursor);

/// <summary>
/// Thin adapter over the host read-only port. Its only jobs: clamp what a browser asks for, map the
/// host projection to console DTOs, and report whether the host granted the port at all.
/// <para>
/// It holds <see cref="IConversationQuery"/> — and therefore performs no SQL of its own. When the
/// host did not grant <c>chat.read</c> the service is absent and every data route answers
/// <c>host.conversation-query.unavailable</c> instead of degrading into direct database access.
/// </para>
/// </summary>
public sealed class ConversationConsoleQueryGateway
{
    /// <summary>Largest message body returned to the browser; longer bodies are marked truncated.</summary>
    public const int MaxMessageCharacters = 8000;

    private readonly IConversationQuery? _query;

    public ConversationConsoleQueryGateway(IConversationQuery? query)
        => _query = query;

    /// <summary>The host injected a port. This is not the same as "the port can return data".</summary>
    public bool IsInjected => _query is not null;

    /// <summary>The injected port has a real data source (not the empty fallback).</summary>
    public bool HasDataSource => _query is not null && _query.HasDataSource;

    /// <summary>
    /// True only when the port is injected AND backed by data. An empty port is "injected but
    /// unavailable"; reporting it as available was a fake-green health check (A-02).
    /// </summary>
    public bool IsAvailable => HasDataSource;

    public string Source => _query is null
        ? "unavailable"
        : _query.HasDataSource ? "host.conversation-query" : "empty";

    /// <summary>Lists conversations. Returns null when the host port is unavailable.</summary>
    public async ValueTask<ConversationConsolePage?> ListAsync(
        ConversationFilter filter,
        CancellationToken cancellationToken = default)
    {
        if (_query is null || !_query.HasDataSource)
            return null;

        var page = await _query.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        return new ConversationConsolePage(
            page.Items
                .Select(item => new ConversationConsoleSession(
                    item.SessionId,
                    item.SourceKey,
                    item.Protocol,
                    item.Platform,
                    item.ConversationId,
                    item.GuildId,
                    item.ActorId,
                    item.MessageCount,
                    item.FirstMessageAt,
                    item.LastMessageAt))
                .ToArray(),
            page.NextCursor);
    }

    /// <summary>
    /// Loads messages after <paramref name="afterMessageId"/> (incremental paging). The session id is
    /// passed through verbatim; the port scopes the query to that session, so one conversation can
    /// never return another's rows.
    /// </summary>
    public async ValueTask<IReadOnlyList<ConversationConsoleMessage>?> GetMessagesAsync(
        string sessionId,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (_query is null || !_query.HasDataSource)
            return null;

        var messages = await _query
            .GetMessagesAsync(sessionId, afterMessageId, limit, cancellationToken)
            .ConfigureAwait(false);

        return messages
            .Select(message =>
            {
                var text = message.Text ?? string.Empty;
                var truncated = text.Length > MaxMessageCharacters;
                return new ConversationConsoleMessage(
                    message.MessageId,
                    message.Role,
                    truncated ? text[..MaxMessageCharacters] : text,
                    message.CreatedAt,
                    truncated);
            })
            .ToArray();
    }
}
