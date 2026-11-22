using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Application.Chat;
using Natter.Feature.Ai;
using Natter.Plugin.Abstractions;

namespace Natter.Feature.Ai.Spark;

/// <summary>NPS feature plugin: Spark provider plus the shared AiChatResponder.</summary>
public sealed class SparkPlugin : INatterPlugin
{
    public const string ApiKeySecretName = "SPARK_API_KEY";

    private readonly IChatClient? _chatClient;

    public SparkPlugin()
    {
    }

    public SparkPlugin(IChatClient chatClient)
        => _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(SparkPlugin).Assembly,
        "Natter.Feature.Ai.Spark.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Config.Get<SparkOptions>();
        Validate(options);

        if (!SecretReference.TryParse(options.ApiKey, out var secret)
            || !string.Equals(secret.Name, ApiKeySecretName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Spark config 'apiKey' must be '${secret:SPARK_API_KEY}'; literal keys are not accepted.");
        }

        var apiKey = context.Secrets.GetRequired(ApiKeySecretName);
        var responderOptions = new AiResponderOptions
        {
            ProviderName = "spark",
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
        services.AddSingleton(responderOptions);
        services.AddSingleton<SparkMetrics>();
        services.AddSingleton<AiConversationStore>();
        services.AddSingleton<IAiConversationStore>(provider =>
            provider.GetService<IConversationStore>() is { } host
                ? new HostConversationStoreAdapter(host, responderOptions)
                : provider.GetRequiredService<AiConversationStore>());
        services.AddSingleton<AiConversationGate>();
        services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<IChatClient>(provider => _chatClient ?? new SparkChatClient(
            provider.GetRequiredService<HttpClient>(),
            options,
            apiKey,
            provider.GetRequiredService<SparkMetrics>()));
        services.AddSingleton<IAiContextAssembler>(provider => new RecentWindowContextAssembler(responderOptions));
        // P5.2c: Spark's tool-calling support is not verified, so the provider explicitly stays on
        // the text protocol (TextOnly); the worker falls back to <tool_call> or answers directly.
        services.AddSingleton<AiChatResponder>(provider => new AiChatResponder(
            provider.GetRequiredService<IChatClient>(),
            responderOptions,
            provider.GetRequiredService<IAiConversationStore>(),
            provider.GetRequiredService<AiConversationGate>(),
            provider.GetRequiredService<Natter.Chat.ChatCommandFactory>(),
            provider.GetRequiredService<IPluginLog>(),
            provider.GetRequiredService<IAiContextAssembler>(),
            provider.GetService<Natter.Application.Chat.IContextPipeline>(),
            Natter.Application.Chat.ToolProtocolCapabilities.TextOnly));
        services.AddTransient<SparkStatusCommandHandler>();

        context.Contributions.AddChatResponder<AiChatResponder>("spark", priority: 20);
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "spark status",
            Description = "Show Spark provider configuration and usage metrics.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => provider.GetRequiredService<SparkStatusCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private static void Validate(SparkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)
            || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Spark config 'baseUrl' must be an absolute http(s) URL.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Spark config 'model' must not be empty.");
        if (options.Thinking is not ("disabled" or "enabled" or "auto"))
            throw new InvalidOperationException("Spark config 'thinking' must be disabled/enabled/auto.");
        if (options.Streaming)
            throw new InvalidOperationException("Spark streaming is not enabled in this Natter version.");
        if (options.Tools is { Count: > 0 })
            throw new InvalidOperationException("Spark tools are not enabled in this Natter version.");
        if (options.MaxOutputTokens is < 1 or > 32768)
            throw new InvalidOperationException("Spark config 'maxOutputTokens' is out of range.");
        if (options.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Spark config 'timeoutSeconds' must be between 1 and 600.");
        if (options.MaxRetries is < 0 or > 5)
            throw new InvalidOperationException("Spark config 'maxRetries' must be between 0 and 5.");
        if (options.HistoryScope is not ("conversation" or "user"))
            throw new InvalidOperationException("Spark config 'historyScope' must be 'conversation' or 'user'.");
    }
}

public sealed class SparkStatusCommandHandler(SparkOptions options, SparkMetrics metrics, IConversationStore history)
    : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var historyCount = await history.CountAsync(cancellationToken).ConfigureAwait(false);
        var host = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
        var snapshot = new SparkStatusSnapshot(
            "iflytek-spark",
            options.Model,
            options.Thinking,
            host,
            options.Streaming,
            options.HistoryMaxMessages,
            historyCount,
            metrics.Snapshot());

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return CliExitCodes.Success;
        }

        invocation.Console.Out.WriteLine("provider: iflytek-spark");
        invocation.Console.Out.WriteLine($"model: {snapshot.Model}");
        invocation.Console.Out.WriteLine($"thinking: {snapshot.Thinking}");
        invocation.Console.Out.WriteLine($"baseUrlHost: {snapshot.BaseUrlHost}");
        invocation.Console.Out.WriteLine($"historyConversations: {snapshot.HistoryConversations}");
        invocation.Console.Out.WriteLine($"requests: {snapshot.Metrics.Requests}");
        invocation.Console.Out.WriteLine($"errors: {snapshot.Metrics.Errors}");
        invocation.Console.Out.WriteLine($"p50LatencyMs: {snapshot.Metrics.P50LatencyMs?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"p95LatencyMs: {snapshot.Metrics.P95LatencyMs?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"inputTokens: {snapshot.Metrics.InputTokens}");
        invocation.Console.Out.WriteLine($"outputTokens: {snapshot.Metrics.OutputTokens}");
        invocation.Console.Out.WriteLine($"lastError: {snapshot.Metrics.LastError ?? "-"}");
        return CliExitCodes.Success;
    }
}

public sealed record SparkStatusSnapshot(
    string Provider,
    string Model,
    string Thinking,
    string BaseUrlHost,
    bool Streaming,
    int HistoryMaxMessages,
    int HistoryConversations,
    SparkMetricsSnapshot Metrics);
