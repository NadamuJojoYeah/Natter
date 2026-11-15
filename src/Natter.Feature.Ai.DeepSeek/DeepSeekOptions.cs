namespace Natter.Feature.Ai.DeepSeek;

/// <summary>NPS-bound DeepSeek responder configuration. The API key is a secret reference.</summary>
public sealed class DeepSeekOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-flash";

    public string Thinking { get; set; } = "disabled";

    public string ReasoningEffort { get; set; } = "none";

    public string SystemPrompt { get; set; } = "You are Natter, a concise Discord assistant.";

    public double Temperature { get; set; } = 0.7;

    public int MaxOutputTokens { get; set; } = 1024;

    public int MaxInputCharacters { get; set; } = 12000;

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxCharacters { get; set; } = 24000;

    public int TimeoutSeconds { get; set; } = 60;

    public bool Streaming { get; set; }

    public List<string> Tools { get; set; } = [];

    public string HistoryScope { get; set; } = "conversation";

    public int MaxConcurrentRequests { get; set; } = 2;

    public string FallbackText { get; set; } = "AI is temporarily unavailable. Please try again shortly.";

    public string EmptyResponseText { get; set; } = "I could not produce a response.";

    public bool IsThinkingEnabled
        => string.Equals(Thinking, "enabled", StringComparison.OrdinalIgnoreCase);

    public bool IsConversationScoped
        => !string.Equals(HistoryScope, "user", StringComparison.OrdinalIgnoreCase);
}
