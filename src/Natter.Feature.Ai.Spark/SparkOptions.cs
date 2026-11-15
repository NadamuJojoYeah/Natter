namespace Natter.Feature.Ai.Spark;

public sealed class SparkOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://spark-api-open.xf-yun.com/v1";

    public string Model { get; set; } = "generalv3.5";

    public string SystemPrompt { get; set; } = "You are Natter, a concise assistant.";

    public double Temperature { get; set; } = 0.7;

    public int MaxOutputTokens { get; set; } = 1024;

    public int MaxInputCharacters { get; set; } = 12000;

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxCharacters { get; set; } = 24000;

    public string HistoryScope { get; set; } = "conversation";

    public int MaxConcurrentRequests { get; set; } = 2;

    public int TimeoutSeconds { get; set; } = 60;

    public int MaxRetries { get; set; } = 2;

    public int RetryBaseDelayMs { get; set; } = 250;

    public int RetryMaxDelayMs { get; set; } = 2000;

    public bool Streaming { get; set; }

    public List<string> Tools { get; set; } = [];

    public string FallbackText { get; set; } = "AI is temporarily unavailable. Please try again shortly.";

    public string EmptyResponseText { get; set; } = "I could not produce a response.";
}
