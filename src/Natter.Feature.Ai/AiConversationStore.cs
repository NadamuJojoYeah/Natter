using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>In-memory, process-local conversation history. Restart is intentionally lossy in v1.</summary>
public sealed class AiConversationStore(AiResponderOptions options) : IAiConversationStore
{
    private readonly AiResponderOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Dictionary<string, ConversationHistory> _histories = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string GetKey(Source source, Conversation conversation, string senderId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(conversation);

        var key = $"{source.Key}|{conversation.Id}";
        return _options.IsConversationScoped ? key : $"{key}|{senderId}";
    }

    public ValueTask<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(
                _histories.TryGetValue(key, out var history)
                    ? history.Messages.ToArray()
                    : []);
        }
    }

    public ValueTask AppendTurnAsync(
        string key,
        string userText,
        string assistantText,
        CancellationToken cancellationToken = default)
        => AppendTurnAsync(key, userText, assistantText, null, cancellationToken);

    public ValueTask AppendTurnAsync(
        string key,
        string userText,
        string assistantText,
        string? sourceEventId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_histories.TryGetValue(key, out var history))
            {
                history = new ConversationHistory();
                _histories[key] = history;
            }

            history.Messages.Add(new ChatMessage(ChatRole.User, userText));
            history.Messages.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            Trim(history);
        }
        return ValueTask.CompletedTask;
    }

    public void Clear()
    {
        lock (_gate)
            _histories.Clear();
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _histories.Count;
        }
    }

    private void Trim(ConversationHistory history)
    {
        while (history.Messages.Count > Math.Max(0, _options.HistoryMaxMessages))
            history.Messages.RemoveRange(0, Math.Min(2, history.Messages.Count));

        var characters = history.Messages.Sum(message => message.Text?.Length ?? 0);
        while (characters > Math.Max(0, _options.HistoryMaxCharacters) && history.Messages.Count > 0)
        {
            var remove = Math.Min(2, history.Messages.Count);
            characters -= history.Messages.Take(remove).Sum(message => message.Text?.Length ?? 0);
            history.Messages.RemoveRange(0, remove);
        }
    }

    private sealed class ConversationHistory
    {
        public List<ChatMessage> Messages { get; } = [];
    }
}
