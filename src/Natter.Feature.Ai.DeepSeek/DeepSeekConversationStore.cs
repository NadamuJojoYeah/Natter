using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Feature.Ai.DeepSeek;

/// <summary>In-memory, process-local conversation history. Restart is intentionally lossy in v1.</summary>
public sealed class DeepSeekConversationStore(DeepSeekOptions options)
{
    private readonly DeepSeekOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Dictionary<string, ConversationHistory> _histories = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string GetKey(Source source, Conversation conversation, string senderId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(conversation);

        var key = $"{source.Key}|{conversation.Id}";
        return _options.IsConversationScoped ? key : $"{key}|{senderId}";
    }

    public IReadOnlyList<ChatMessage> GetMessages(string key)
    {
        lock (_gate)
        {
            return _histories.TryGetValue(key, out var history)
                ? history.Messages.ToArray()
                : [];
        }
    }

    public void AppendTurn(string key, string userText, string assistantText)
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
