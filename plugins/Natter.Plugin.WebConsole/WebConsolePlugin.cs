using Microsoft.Extensions.DependencyInjection;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.WebConsole;

/// <summary>NPS v1 entry point. Configure only stages services; the listener starts in ActivateAsync.</summary>
public sealed class WebConsolePlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(WebConsolePlugin).Assembly,
        "Natter.Plugin.WebConsole.plugin.json");

    private WebConsoleRuntime? _runtime;
    private IDisposable? _stopRegistration;

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = LoadOptions(context);
        var issues = WebConsoleOptionsValidator.Validate(options);
        if (issues.Count > 0)
        {
            throw new WebConsoleStartupException(
                issues[0].Code,
                string.Join("; ", issues.Select(issue => $"{issue.Pointer} {issue.Message}")));
        }

        var pluginDirectory = WebConsolePluginPaths.ResolvePluginDirectory(typeof(WebConsolePlugin).Assembly.Location);
        var schemaPath = string.IsNullOrWhiteSpace(context.Manifest.ConfigSchema)
            ? null
            : Path.Combine(pluginDirectory, context.Manifest.ConfigSchema);

        var configPreview = WebConsoleConfig.Build(
            context.Config.Raw,
            source: "plugin.config",
            schemaPath,
            options);

        var identity = new WebConsoleIdentity(
            context.Manifest.Id,
            context.Manifest.Name,
            context.Manifest.Version,
            context.Manifest.Kind.ToString().ToLowerInvariant(),
            context.Manifest.HostApi,
            typeof(WebConsolePlugin).Assembly.GetName().Name ?? "Natter.Plugin.WebConsole",
            pluginDirectory,
            Path.Combine(pluginDirectory, "plugin.json"));

        _runtime = new WebConsoleRuntime(identity, options, configPreview, context.Log, TimeProvider.System);

        var services = context.Services.Collection;
        services.AddSingleton(_runtime);
        services.AddSingleton<WebStatusCommand>();

        context.Contributions.AddCliCommand(new CliCommandDescriptor
        {
            Name = "web status",
            Description = "Show the WebConsole plugin server state, host data source and registered gaps.",
            Options = [new CliOptionDescriptor("json", "Emit machine-readable JSON.")],
            HandlerFactory = services => services.GetRequiredService<WebStatusCommand>(),
        });

        return ValueTask.CompletedTask;
    }

    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runtime = _runtime
                      ?? throw new InvalidOperationException("WebConsole plugin was not configured.");

        var provider = context.Services.Provider
                       ?? throw new InvalidOperationException("The plugin provider is not available during activation.");

        await runtime.StartAsync(context.Secrets, provider, cancellationToken).ConfigureAwait(false);

        _stopRegistration = context.Lifetime.OnStop(
            token => new ValueTask(StopSafelyAsync(runtime, token)));
    }

    public async ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _stopRegistration?.Dispose();
        _stopRegistration = null;

        if (_runtime is not null)
            await _runtime.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopSafelyAsync(WebConsoleRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Stop is best-effort; the host must not stay alive because of this plugin.
        }
    }

    private static WebConsoleOptions LoadOptions(IPluginContext context)
    {
        try
        {
            return context.Config.Get<WebConsoleOptions>();
        }
        catch (PluginConfigException exception)
        {
            throw new WebConsoleStartupException("webconsole.config-invalid", exception.Message);
        }
    }
}
