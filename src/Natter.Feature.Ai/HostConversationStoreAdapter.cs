using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>Adapts the host-owned IConversationStore to the provider-neutral AI history boundary.</summary>
public sealed class HostConversationStoreAdapter(
    IConversationStore store,
    AiResponderOptions options) : IAiConversationStore
{
    private readonly IConversationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly AiResponderOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private const char Separator = '\u001f';

    public string GetKey(Source source, Conversation conversation, string senderId)
    {
        var actor = IsConversationScoped() ? string.Empty : senderId;
        return string.Join(Separator, source.Key, conversation.Id, conversation.GuildId ?? string.Empty, actor);
    }

    public async ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var messages = await _store.GetRecentAsync(
            ParseKey(key),
            Math.Max(0, Natter.Application.Chat.ProfileContext.Current?.History.MaxMessages ?? _options.HistoryMaxMessages),
            Math.Max(0, Natter.Application.Chat.ProfileContext.Current?.History.MaxCharacters ?? _options.HistoryMaxCharacters),
            cancellationToken).ConfigureAwait(false);

        return messages.Select(message => new ChatMessage(
            string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User,
            message.Text)).ToArray();
    }

    public ValueTask AppendTurnAsync(
        string key,
        string userText,
        string assistantText,
        CancellationToken cancellationToken = default)
        => AppendTurnAsync(key, userText, assistantText, null, cancellationToken);

    public async ValueTask AppendTurnAsync(
        string key,
        string userText,
        string assistantText,
        string? sourceEventId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _store.AppendTurnAsync(
            ParseKey(key),
            new StoredMessage("user", userText, sourceEventId, now),
            new StoredMessage("assistant", assistantText, null, now),
            cancellationToken).ConfigureAwait(false);
    }

    private static Natter.Application.Chat.ConversationKey ParseKey(string key)
    {
        var parts = key.Split(Separator);
        return parts.Length >= 4
            ? new Natter.Application.Chat.ConversationKey(parts[0], parts[1], Null(parts[2]), Null(parts[3]))
            : new Natter.Application.Chat.ConversationKey(key, key, null, null);
    }

    private bool IsConversationScoped()
    {
        var scope = Natter.Application.Chat.ProfileContext.Current?.History.Scope;
        return scope is null
            ? _options.IsConversationScoped
            : !string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Null(string value) => string.IsNullOrEmpty(value) ? null : value;
}
