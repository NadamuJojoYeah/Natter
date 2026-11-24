namespace Natter.Feature.Ai;

/// <summary>Provider-neutral configuration for the shared chat responder.</summary>
/// <summary>
/// User-facing reply text that is not provider specific. Providers must reference these instead of
/// restating the literals: two copies of the same sentence drift the moment one is edited (S3 C-07).
/// </summary>
public static class AiReplyDefaults
{
    /// <summary>Shown when the provider genuinely failed after retries.</summary>
    public const string FallbackText = "AI is temporarily unavailable. Please try again shortly.";

    /// <summary>Shown when the provider returned an empty body.</summary>
    public const string EmptyResponseText = "I could not produce a response.";
}

public sealed class AiResponderOptions
{
    public string ProviderName { get; set; } = "ai";

    public string SystemPrompt { get; set; } = "You are Natter, a concise assistant.";

    public int MaxInputCharacters { get; set; } = 12000;

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxCharacters { get; set; } = 24000;

    public string HistoryScope { get; set; } = "conversation";

    public int MaxConcurrentRequests { get; set; } = 2;

    /// <summary>Maximum extra attempts when a provider response is clipped (0 disables retry).</summary>
    public int MaxClipRetries { get; set; } = 1;

    /// <summary>Base delay between clipped-response retries; retries are bounded and cancelable.</summary>
    public int ClipRetryDelayMilliseconds { get; set; } = 100;

    /// <summary>Maximum idle conversation gates retained by <see cref="AiConversationGate"/>.</summary>
    public int MaxConversationEntries { get; set; } = 1024;

    /// <summary>Idle conversation gates older than this are pruned.</summary>
    public int ConversationIdleSeconds { get; set; } = 600;

    /// <summary>How long Dispose waits for in-flight leases before leaving semaphores for GC.</summary>
    public int GateDisposeTimeoutMilliseconds { get; set; } = 2000;

    public string FallbackText { get; set; } = AiReplyDefaults.FallbackText;

    /// <summary>
    /// Shown when the provider keeps hitting the output ceiling instead of failing outright. Reusing
    /// <see cref="FallbackText"/> here hid a deterministic, fixable condition (a too-small max_tokens)
    /// behind "temporarily unavailable", so users retried a request that could never succeed.
    /// </summary>
    public string ClippedText { get; set; } =
        "The answer was too long for the current output limit, so it was cut off. "
        + "Ask for a smaller chunk, or raise the provider's MaxOutputTokens.";

    public string EmptyResponseText { get; set; } = AiReplyDefaults.EmptyResponseText;

    public bool IsConversationScoped
        => !string.Equals(HistoryScope, "user", StringComparison.OrdinalIgnoreCase);
}
