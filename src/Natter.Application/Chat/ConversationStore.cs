using Natter.Chat;

namespace Natter.Application.Chat;



public sealed record ConversationState(string SessionId, ConversationKey Key, int Version);

/// <summary>Host-owned conversation persistence. Plugins must not own this source of truth.</summary>
public interface IConversationStore
{
    ValueTask<ConversationState> GetOrCreateAsync(ConversationKey key, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredMessage>> GetRecentAsync(
        ConversationKey key,
        int maxMessages,
        int maxCharacters,
        CancellationToken cancellationToken = default);

    ValueTask AppendTurnAsync(
        ConversationKey key,
        StoredMessage userMessage,
        StoredMessage assistantMessage,
        CancellationToken cancellationToken = default);

    ValueTask ClearAsync(ConversationKey key, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StoredMessage>> GetAfterAsync(
        ConversationKey key,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>In-memory host store for embedded hosts/tests; SQLite is used when --state-dir exists.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<ConversationKey, List<StoredMessage>> _sessions = [];
    private readonly Lock _gate = new();
    private long _nextMessageId;

    public ValueTask<ConversationState> GetOrCreateAsync(ConversationKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var messages))
            {
                messages = [];
                _sessions[key] = messages;
            }
            return ValueTask.FromResult(new ConversationState(SessionId(key), key, messages.Count));
        }
    }

    public ValueTask<IReadOnlyList<StoredMessage>> GetRecentAsync(
        ConversationKey key,
        int maxMessages,
        int maxCharacters,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var messages))
                return ValueTask.FromResult<IReadOnlyList<StoredMessage>>([]);

            var result = new List<StoredMessage>();
            var characters = 0;
            for (var index = messages.Count - 1; index >= 0 && result.Count < Math.Max(0, maxMessages); index--)
            {
                var message = messages[index];
                characters += message.Text.Length;
                if (characters > Math.Max(0, maxCharacters) && result.Count > 0)
                    break;
                result.Add(message);
            }
            result.Reverse();
            return ValueTask.FromResult<IReadOnlyList<StoredMessage>>(result);
        }
    }

    public ValueTask AppendTurnAsync(
        ConversationKey key,
        StoredMessage userMessage,
        StoredMessage assistantMessage,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var messages))
            {
                messages = [];
                _sessions[key] = messages;
            }
            messages.Add(WithId(userMessage));
            messages.Add(WithId(assistantMessage));
            while (messages.Count > 1000)
                messages.RemoveRange(0, 2);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(ConversationKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _sessions.Remove(key);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<StoredMessage>> GetAfterAsync(
        ConversationKey key,
        long afterMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var messages))
                return ValueTask.FromResult<IReadOnlyList<StoredMessage>>([]);
            var result = messages
                .Where(message => message.MessageId > afterMessageId)
                .Take(Math.Max(0, limit))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<StoredMessage>>(result);
        }
    }

    public ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_sessions.Count);
    }

    private StoredMessage WithId(StoredMessage message)
        => message.MessageId > 0 ? message : message with { MessageId = ++_nextMessageId };

    public static string SessionId(ConversationKey key)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{key.SourceKey}\n{key.ConversationId}\n{key.GuildId}\n{key.ActorId}")))[..24];
}
