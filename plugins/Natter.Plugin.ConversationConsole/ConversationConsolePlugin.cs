using Microsoft.Extensions.DependencyInjection;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// NPS v1 entry point for the Conversation Console (design §3, P1). Configure only stages services;
/// the loopback listener starts in <see cref="ActivateAsync"/> so a failed bind cannot leave a
/// half-registered plugin behind.
/// </summary>
public sealed class ConversationConsolePlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(ConversationConsolePlugin).Assembly,
        "Natter.Plugin.ConversationConsole.plugin.json");

    private ConversationConsoleRuntime? _runtime;
    private IDisposable? _stopRegistration;

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = LoadOptions(context);
        var issues = ConversationConsoleOptionsValidator.Validate(options);
        if (issues.Count > 0)
        {
            throw new ConversationConsoleStartupException(
                issues[0].Code,
                string.Join("; ", issues.Select(issue => $"{issue.Pointer} {issue.Message}")));
        }

        var pluginDirectory = ConversationConsoleRuntime.ResolvePluginDirectory(typeof(ConversationConsolePlugin).Assembly.Location);

        _runtime = new ConversationConsoleRuntime(options, pluginDirectory, context.Log, TimeProvider.System);
        context.Services.Collection.AddSingleton(_runtime);

        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runtime = _runtime
                      ?? throw new InvalidOperationException("The conversation console was not configured.");

        var provider = context.Services.Provider
                       ?? throw new InvalidOperationException("The plugin provider is not available during activation.");

        await runtime.StartAsync(context.Secrets, provider, cancellationToken).ConfigureAwait(false);

        _stopRegistration = context.Lifetime.OnStop(token => new ValueTask(StopSafelyAsync(runtime, token)));
    }

    public async ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _stopRegistration?.Dispose();
        _stopRegistration = null;

        if (_runtime is not null)
            await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopSafelyAsync(ConversationConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Stop is best-effort: the host must not stay alive because of a diagnostic console.
        }
    }

    private static ConversationConsoleOptions LoadOptions(IPluginContext context)
    {
        try
        {
            return context.Config.Get<ConversationConsoleOptions>() ?? new ConversationConsoleOptions();
        }
        catch (PluginConfigException exception)
        {
            throw new ConversationConsoleStartupException("conversation-console.config-invalid", exception.Message);
        }
    }
}
