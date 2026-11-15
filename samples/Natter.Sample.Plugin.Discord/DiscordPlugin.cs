using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Chat;
using Natter.Chat.Discord;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.Discord;

/// <summary>
/// NPS v1 Discord transport plugin. Configure only registers services/contributions; the
/// NPS runtime calls <see cref="IChatUpstream.ConnectAsync"/> after activation.
/// </summary>
public sealed class DiscordPlugin : INatterPlugin
{
    public const string TokenSecretName = "DISCORD_BOT_TOKEN";

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(DiscordPlugin).Assembly,
        "Natter.Sample.Plugin.Discord.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Config.Get<DiscordOptions>();
        options.Intents ??= ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"];
        options.Guilds ??= [];
        options.Channels ??= [];
        options.Users ??= [];

        if (!SecretReference.TryParse(options.Token, out var tokenReference)
            || !string.Equals(tokenReference.Name, TokenSecretName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Discord config 'token' must be '${secret:DISCORD_BOT_TOKEN}'; literal tokens are not accepted.");
        }

        if (!ulong.TryParse(options.SelfId, out _))
            throw new InvalidOperationException("Discord config 'selfId' must be the numeric bot user id.");

        if (options.MaxMessageLength is < 1 or > 2000)
            throw new InvalidOperationException("Discord config 'maxMessageLength' must be between 1 and 2000.");

        var token = DiscordBotToken.From(context.Secrets.GetRequired(TokenSecretName));
        var source = Source.Create("discord", "discord", options.SelfId);

        var services = context.Services.Collection;
        services.AddSingleton(options);
        services.AddSingleton(token);
        services.AddSingleton(source);
        services.AddSingleton<IDiscordTransport, DiscordSocketTransport>();
        services.AddSingleton<DiscordChatUpstream>();
        services.AddSingleton<DiscordStatusCommandHandler>();

        context.Contributions.AddChatUpstream<DiscordChatUpstream>("discord");
        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "discord status",
            Description = "Show Discord transport connection and capability status.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = provider => provider.GetRequiredService<DiscordStatusCommandHandler>(),
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

/// <summary>CLI handler for <c>discord status</c>; never prints the token.</summary>
public sealed class DiscordStatusCommandHandler(DiscordChatUpstream upstream) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var status = upstream.GetStatus();

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(status, JsonOptions));
            return ValueTask.FromResult(CliExitCodes.Success);
        }

        invocation.Console.Out.WriteLine($"source: {status.Source.Key}");
        invocation.Console.Out.WriteLine($"connected: {status.Connected.ToString().ToLowerInvariant()}");
        invocation.Console.Out.WriteLine($"guilds: {status.GuildCount?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"channels: {status.ChannelCount?.ToString() ?? "-"}");
        invocation.Console.Out.WriteLine($"latency: {status.LatencyMs?.ToString() ?? "-"} ms");
        invocation.Console.Out.WriteLine($"lastError: {status.LastError ?? "-"}");
        invocation.Console.Out.WriteLine($"slashCommands: {status.SlashCommands.ToString().ToLowerInvariant()}");
        invocation.Console.Out.WriteLine(
            $"capabilities: max={status.Capabilities.MaxMessageLength} markdown={status.Capabilities.Markdown} "
            + $"mentions={status.Capabilities.Mentions} edit={status.Capabilities.Edit} delete={status.Capabilities.Delete}");
        invocation.Console.Out.WriteLine($"guilds allowlist: {Format(status.Guilds)}");
        invocation.Console.Out.WriteLine($"channels allowlist: {Format(status.Channels)}");
        invocation.Console.Out.WriteLine($"users allowlist: {Format(status.Users)}");

        return ValueTask.FromResult(CliExitCodes.Success);
    }

    private static string Format(IReadOnlyList<string> values)
        => values.Count == 0 ? "*" : string.Join(",", values);
}
