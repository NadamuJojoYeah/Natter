using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Chat.DingTalk;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.DingTalk;

public sealed class DingTalkPlugin : INatterPlugin
{
    public const string AppSecretName = "DINGTALK_APP_SECRET";

    private readonly IDingTalkTransport? _transport;

    public DingTalkPlugin()
    {
    }

    /// <summary>Test/composition seam; production uses the parameterless NPS entry point.</summary>
    public DingTalkPlugin(IDingTalkTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(DingTalkPlugin).Assembly,
        "Natter.Sample.Plugin.DingTalk.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Config.Get<DingTalkOptions>();
        if (!SecretReference.TryParse(options.AppSecret, out var secret)
            || !string.Equals(secret.Name, AppSecretName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DingTalk config 'appSecret' must be '${secret:DINGTALK_APP_SECRET}'; literal secrets are not accepted.");
        }

        if (string.IsNullOrWhiteSpace(options.BotId))
            throw new InvalidOperationException("DingTalk config 'botId' must not be empty.");

        var appSecret = context.Secrets.GetRequired(AppSecretName);
        var source = Source.Create("dingtalk", "dingtalk", options.BotId);
        var services = context.Services.Collection;

        services.AddSingleton(options);
        services.AddSingleton(source);
        services.AddSingleton<IDingTalkTransport>(provider => _transport ?? new DingTalkWebhookTransport(options, appSecret));
        services.AddSingleton<DingTalkChatUpstream>();
        services.AddTransient<DingTalkStatusCommandHandler>();

        context.Contributions.AddChatUpstream<DingTalkChatUpstream>("dingtalk");
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "dingtalk status",
            Description = "Show DingTalk callback/upstream status.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => provider.GetRequiredService<DingTalkStatusCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed class DingTalkStatusCommandHandler(DingTalkOptions options, DingTalkChatUpstream upstream)
    : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var snapshot = new
        {
            provider = "dingtalk",
            source = upstream.Source.Key,
            connected = upstream.IsConnected,
            botId = options.BotId,
            listenPrefix = options.ListenPrefix,
            requireMention = options.RequireMention,
            conversations = options.Conversations,
            users = options.Users,
        };

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        invocation.Console.Out.WriteLine($"source: {snapshot.source}");
        invocation.Console.Out.WriteLine($"connected: {snapshot.connected.ToString().ToLowerInvariant()}");
        invocation.Console.Out.WriteLine($"listenPrefix: {snapshot.listenPrefix}");
        invocation.Console.Out.WriteLine($"requireMention: {snapshot.requireMention.ToString().ToLowerInvariant()}");
        invocation.Console.Out.WriteLine($"conversations: {(snapshot.conversations.Count == 0 ? "*" : string.Join(",", snapshot.conversations))}");
        invocation.Console.Out.WriteLine($"users: {(snapshot.users.Count == 0 ? "*" : string.Join(",", snapshot.users))}");
        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
