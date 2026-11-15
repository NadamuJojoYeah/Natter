namespace Natter.Feature.Ai;

/// <summary>Provider-neutral configuration for the shared chat responder.</summary>
public sealed class AiResponderOptions
{
    public string ProviderName { get; set; } = "ai";

    public string SystemPrompt { get; set; } = "You are Natter, a concise assistant.";

    public int MaxInputCharacters { get; set; } = 12000;

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxCharacters { get; set; } = 24000;

    public string HistoryScope { get; set; } = "conversation";

    public int MaxConcurrentRequests { get; set; } = 2;

    public string FallbackText { get; set; } = "AI is temporarily unavailable. Please try again shortly.";

    public string EmptyResponseText { get; set; } = "I could not produce a response.";

    public bool IsConversationScoped
        => !string.Equals(HistoryScope, "user", StringComparison.OrdinalIgnoreCase);
}
