using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Application.Admin;

namespace Natter.Infrastructure.Admin;

/// <summary>
/// Registers the host admin surface: shared redaction, the JSONL audit sink, the bounded host log
/// tail and the admin config store. Services are only bound into a plugin provider by
/// <c>NpsPluginRuntime</c> when the plugin holds the matching admin permission.
/// </summary>
public sealed class AdminCompositionModule : ICompositionModule
{
    private readonly Func<Action<AdminOptions>?>? _configurationProvider;

    public AdminCompositionModule(Func<Action<AdminOptions>?>? configurationProvider = null)
        => _configurationProvider = configurationProvider;

    public string Name => "Framework.Admin";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        var options = AdminOptions.FromComposition(context.Options);
        _configurationProvider?.Invoke()?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IAdminRedactor>(AdminRedactor.Default);
        services.AddSingleton<IAdminAuditSink>(provider => new JsonlAdminAuditSink(
            options,
            provider.GetRequiredService<IAdminRedactor>(),
            provider.GetRequiredService<ILogger<JsonlAdminAuditSink>>()));

        if (!options.EnableAdminSurface)
            return;

        services.AddSingleton(provider => new AdminLogBuffer(options.MaxLogEntries, options.MaxLogMessageLength));
        services.AddSingleton<ILoggerProvider>(provider => new AdminLogBufferLoggerProvider(
            provider.GetRequiredService<AdminLogBuffer>(),
            provider.GetRequiredService<IAdminRedactor>()));
        services.AddSingleton<IAdminLogReader>(provider => new HostAdminLogReader(
            provider.GetRequiredService<AdminLogBuffer>(),
            options,
            provider.GetRequiredService<IAdminAuditSink>(),
            provider.GetRequiredService<ILogger<HostAdminLogReader>>()));

        services.AddSingleton<IAdminConfigSchemaValidator>(provider => new HostAdminConfigSchemaValidator(
            provider.GetRequiredService<Natter.Abstractions.Plugins.IPluginCatalog>(),
            provider.GetRequiredService<ILogger<HostAdminConfigSchemaValidator>>()));
        services.AddSingleton<IAdminConfigStore>(provider => new HostAdminConfigStore(
            options,
            provider.GetRequiredService<Natter.Abstractions.Plugins.IPluginCatalog>(),
            provider.GetRequiredService<IAdminConfigSchemaValidator>(),
            provider.GetRequiredService<IAdminAuditSink>(),
            provider.GetRequiredService<ILogger<HostAdminConfigStore>>()));
    }
}
