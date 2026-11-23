namespace Natter.Feature.Ai.DeepSeek;

/// <summary>NPS-bound DeepSeek responder configuration. The API key is a secret reference.</summary>
/// <summary>Code defaults for the parameters surfaced by <c>ai status</c> (single source).</summary>
public static class DeepSeekDefaults
{
    public const int MaxOutputTokens = 4096;
    public const int MaxInputCharacters = 12000;
    public const double Temperature = 0.7;
    public const int TimeoutSeconds = 60;
    public const int MaxRetries = 2;
}

public sealed class DeepSeekOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-flash";

    public string Thinking { get; set; } = "disabled";

    public string ReasoningEffort { get; set; } = "none";

    public string SystemPrompt { get; set; } = "You are Natter, a concise Discord assistant.";

    public double Temperature { get; set; } = DeepSeekDefaults.Temperature;

    // 1024 was far too low: a 150-line code block (~5k tokens) was clipped on every attempt, retried
    // three times, and then surfaced as the generic "AI is temporarily unavailable" — the user saw an
    // outage where the real cause was our own token ceiling. DeepSeek accepts far more; 4096 covers
    // normal chat plus a full code block without inviting runaway generations.
    public int MaxOutputTokens { get; set; } = DeepSeekDefaults.MaxOutputTokens;

    public int MaxInputCharacters { get; set; } = DeepSeekDefaults.MaxInputCharacters;

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxCharacters { get; set; } = 24000;

    public int TimeoutSeconds { get; set; } = DeepSeekDefaults.TimeoutSeconds;

    /// <summary>Maximum retries after the initial attempt for retryable provider failures.</summary>
    public int MaxRetries { get; set; } = DeepSeekDefaults.MaxRetries;

    public int RetryBaseDelayMs { get; set; } = 250;

    public int RetryMaxDelayMs { get; set; } = 2000;

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
