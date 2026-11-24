namespace Natter.Feature.Ai.Spark;

/// <summary>
/// Numeric bounds enforced at activation. <c>config.schema.json</c> declares the same numbers; a test
/// compares the two so "schema accepts, activation throws" cannot happen silently (S3 D-04).
/// </summary>
public static class SparkNumericBounds
{
    public static readonly IReadOnlyDictionary<string, (long Min, long Max)> All =
        new Dictionary<string, (long Min, long Max)>(System.StringComparer.Ordinal)
        {
            ["maxOutputTokens"] = (1, 32768),
            ["timeoutSeconds"] = (1, 600),
            ["maxRetries"] = (0, 5),
            ["retryBaseDelayMs"] = (0, 60000),
            ["retryMaxDelayMs"] = (0, 60000),
            ["maxConcurrentRequests"] = (1, 16),
            ["historyMaxMessages"] = (0, 1000),
            ["historyMaxCharacters"] = (0, 1000000),
        };
}

public sealed class SparkOptions
{
    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://maas-api.cn-huabei-1.xf-yun.com/v2";

    public string Model { get; set; } = "spark-x2.5-1.7b";

    public string Thinking { get; set; } = "disabled";

    public string SystemPrompt { get; set; } = "You are Natter, a concise assistant.";

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

    public string FallbackText { get; set; } = AiReplyDefaults.FallbackText;

    public string EmptyResponseText { get; set; } = AiReplyDefaults.EmptyResponseText;
}
