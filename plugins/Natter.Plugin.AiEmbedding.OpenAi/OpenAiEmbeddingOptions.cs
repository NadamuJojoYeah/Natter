namespace Natter.Plugin.AiEmbedding.OpenAi;

/// <summary>Configuration for an OpenAI-compatible embedding endpoint.</summary>
public sealed class OpenAiEmbeddingOptions
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "text-embedding-3-small";

    /// <summary>Optional output dimensions; omitted from the request when null.</summary>
    public int? Dimensions { get; set; }

    /// <summary>Must be a <c>${secret:NAME}</c> reference; literal keys are rejected.</summary>
    public string ApiKey { get; set; } = "${secret:OPENAI_COMPAT_API_KEY}";

    public int MaxBatchSize { get; set; } = 64;

    public int MaxInputTokens { get; set; } = 8192;

    public int TimeoutSeconds { get; set; } = 60;
}
