using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>
/// Provider-neutral context boundary. Providers and transports never build prompts directly;
/// they share this assembler through the responder.
/// </summary>
public interface IAiContextAssembler
{
    ValueTask<IReadOnlyList<ChatMessage>> AssembleAsync(
        Source source,
        Conversation conversation,
        User sender,
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}

/// <summary>Default recent-window assembler: stable system prompt + history + current user.</summary>
public sealed class RecentWindowContextAssembler(AiResponderOptions options) : IAiContextAssembler
{
    private readonly AiResponderOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public ValueTask<IReadOnlyList<ChatMessage>> AssembleAsync(
        Source source,
        Conversation conversation,
        User sender,
        string userText,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var max = Math.Max(1, _options.MaxInputCharacters);
        var text = userText.Length <= max ? userText : userText[..max];
        var messages = new List<ChatMessage>(history.Count + 2);

        if (!string.IsNullOrWhiteSpace(_options.SystemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, _options.SystemPrompt));

        messages.AddRange(history);
        messages.Add(new ChatMessage(ChatRole.User, text));
        return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }
}
