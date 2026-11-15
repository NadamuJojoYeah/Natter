using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Feature.Ai;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Feature.Ai.DeepSeek;

/// <summary>
/// NPS feature plugin containing the DeepSeek provider and responder. Configure only wires
/// services/contributions; no network call happens before NPS activates the upstream loop.
/// </summary>
public sealed class DeepSeekPlugin : INatterPlugin
{
    public const string ApiKeySecretName = "DEEPSEEK_API_KEY";

    private readonly IChatClient? _chatClient;

    public DeepSeekPlugin()
    {
    }

    /// <summary>Test/composition seam; production uses the parameterless NPS entry point.</summary>
    public DeepSeekPlugin(IChatClient chatClient)
        => _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(DeepSeekPlugin).Assembly,
        "Natter.Feature.Ai.DeepSeek.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Config.Get<DeepSeekOptions>();
        Validate(options);

        if (!SecretReference.TryParse(options.ApiKey, out var secret)
            || !string.Equals(secret.Name, ApiKeySecretName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DeepSeek config 'apiKey' must be '${secret:DEEPSEEK_API_KEY}'; literal keys are not accepted.");
        }

        var apiKey = DeepSeekApiKey.From(context.Secrets.GetRequired(ApiKeySecretName));
        var responderOptions = new AiResponderOptions
        {
            ProviderName = "deepseek",
            SystemPrompt = options.SystemPrompt,
            MaxInputCharacters = options.MaxInputCharacters,
            HistoryMaxMessages = options.HistoryMaxMessages,
            HistoryMaxCharacters = options.HistoryMaxCharacters,
            HistoryScope = options.HistoryScope,
            MaxConcurrentRequests = options.MaxConcurrentRequests,
            FallbackText = options.FallbackText,
            EmptyResponseText = options.EmptyResponseText,
        };
        var services = context.Services.Collection;

        services.AddSingleton(options);
        services.AddSingleton(apiKey);
        services.AddSingleton(responderOptions);
        services.AddSingleton<DeepSeekMetrics>();
        services.AddSingleton<AiConversationStore>();
        services.AddSingleton<IAiConversationStore>(provider => provider.GetRequiredService<AiConversationStore>());
        services.AddSingleton<AiConversationGate>();
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<IChatClient>(provider => _chatClient ?? new DeepSeekChatClient(
            provider.GetRequiredService<HttpClient>(),
            options,
            apiKey,
            provider.GetRequiredService<DeepSeekMetrics>()));
        services.AddSingleton<IAiContextAssembler>(provider => new RecentWindowContextAssembler(responderOptions));
        services.AddSingleton<AiChatResponder>();
        services.AddTransient<DeepSeekStatusCommandHandler>();

        context.Contributions.AddChatResponder<AiChatResponder>("deepseek", priority: 10);
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "ai status",
            Description = "Show DeepSeek provider configuration and usage metrics.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => provider.GetRequiredService<DeepSeekStatusCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private static void Validate(DeepSeekOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("DeepSeek config 'baseUrl' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("DeepSeek config 'model' must not be empty.");

        if (options.Thinking is not ("disabled" or "enabled"))
            throw new InvalidOperationException("DeepSeek config 'thinking' must be 'disabled' or 'enabled'.");

        if (options.ReasoningEffort is not ("none" or "low" or "high" or "max"))
            throw new InvalidOperationException("DeepSeek config 'reasoningEffort' must be none/low/high/max.");

        if (options.Streaming)
            throw new InvalidOperationException("DeepSeek streaming is not enabled in this Natter version.");

        if (options.Tools is { Count: > 0 })
            throw new InvalidOperationException("DeepSeek tools are not enabled in this Natter version.");

        if (options.MaxOutputTokens is < 1 or > 384000)
            throw new InvalidOperationException("DeepSeek config 'maxOutputTokens' is out of range.");

        if (options.HistoryMaxMessages < 0 || options.HistoryMaxCharacters < 0)
            throw new InvalidOperationException("DeepSeek history limits must not be negative.");

        if (options.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("DeepSeek config 'timeoutSeconds' must be between 1 and 600.");

        if (options.MaxRetries is < 0 or > 5)
            throw new InvalidOperationException("DeepSeek config 'maxRetries' must be between 0 and 5.");

        if (options.RetryBaseDelayMs is < 0 or > 60000 || options.RetryMaxDelayMs < options.RetryBaseDelayMs)
            throw new InvalidOperationException("DeepSeek retry delays are invalid.");

        if (options.MaxConcurrentRequests is < 1 or > 16)
            throw new InvalidOperationException("DeepSeek config 'maxConcurrentRequests' must be between 1 and 16.");

        if (options.HistoryScope is not ("conversation" or "user"))
            throw new InvalidOperationException("DeepSeek config 'historyScope' must be 'conversation' or 'user'.");
    }
}

/// <summary>CLI handler for <c>ai status</c>; token/key never appears in the payload.</summary>
public sealed class DeepSeekStatusCommandHandler(
    DeepSeekOptions options,
    DeepSeekMetrics metrics,
    AiConversationStore history) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var baseHost = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
        var snapshot = new DeepSeekStatusSnapshot(
            "deepseek",
            options.Model,
            options.Thinking,
            options.ReasoningEffort,
            baseHost,
            options.Streaming,
            options.HistoryMaxMessages,
            options.HistoryMaxCharacters,
            history.Count,
            metrics.Snapshot());

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        invocation.Console.Out.WriteLine($"provider: deepseek");
        invocation.Console.Out.WriteLine($"model: {snapshot.Model}");
        invocation.Console.Out.WriteLine($"thinking: {snapshot.Thinking}");
        invocation.Console.Out.WriteLine($"reasoningEffort: {snapshot.ReasoningEffort}");
        invocation.Console.Out.WriteLine($"baseUrlHost: {snapshot.BaseUrlHost}");
        invocation.Console.Out.WriteLine($"historyConversations: {snapshot.HistoryConversations}");
        invocation.Console.Out.WriteLine($"requests: {snapshot.Metrics.Requests}");
        invocation.Console.Out.WriteLine($"errors: {snapshot.Metrics.Errors}");
        invocation.Console.Out.WriteLine($"p50LatencyMs: {snapshot.Metrics.P50LatencyMs?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"p95LatencyMs: {snapshot.Metrics.P95LatencyMs?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"inputTokens: {snapshot.Metrics.InputTokens}");
        invocation.Console.Out.WriteLine($"outputTokens: {snapshot.Metrics.OutputTokens}");
        invocation.Console.Out.WriteLine($"cachedInputTokens: {snapshot.Metrics.CachedInputTokens}");
        invocation.Console.Out.WriteLine($"reasoningTokens: {snapshot.Metrics.ReasoningTokens}");
        invocation.Console.Out.WriteLine($"lastError: {snapshot.Metrics.LastError ?? "-"}");

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}

public sealed record DeepSeekStatusSnapshot(
    string Provider,
    string Model,
    string Thinking,
    string ReasoningEffort,
    string BaseUrlHost,
    bool Streaming,
    int HistoryMaxMessages,
    int HistoryMaxCharacters,
    int HistoryConversations,
    DeepSeekMetricsSnapshot Metrics);
