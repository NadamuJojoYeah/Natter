using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.StateVitals.Tests;

internal sealed class TestPluginConfig(string json) : IPluginConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public JsonElement Raw { get; } = JsonDocument.Parse(json).RootElement.Clone();

    public T Get<T>() where T : class
        => Raw.Deserialize<T>(Options) ?? throw new PluginConfigException("config is null");

    public bool TryGet<T>(out T? value) where T : class
    {
        value = Raw.Deserialize<T>(Options);
        return value is not null;
    }

    public bool TryGetSecretReference(string jsonPointer, out SecretReference? reference)
    {
        reference = null;
        return false;
    }
}

internal sealed class TestPluginSecrets : IPluginSecrets
{
    public string GetRequired(string name) => string.Empty;

    public string? Get(string name) => null;
}

internal sealed class TestPluginLog : IPluginLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public void Debug(string message, params object?[] args) => Add("debug", message, args);

    public void Information(string message, params object?[] args) => Add("info", message, args);

    public void Warning(string message, params object?[] args) => Add("warning", message, args);

    public void Error(Exception? exception, string message, params object?[] args) => Add("error", message, args);

    private void Add(string level, string message, object?[] args)
    {
        var rendered = args.Length == 0
            ? message
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, message, args);
        _entries.Add($"[{level}] {rendered}");
    }
}

internal sealed class TestLifetime : IPluginLifetime
{
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken Stopping => _stopping.Token;

    public IDisposable OnStop(Func<CancellationToken, ValueTask> callback) => new Registration(callback, _stopping.Token);

    private sealed class Registration(Func<CancellationToken, ValueTask> callback, CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenRegistration _registration = token.Register(() => callback(token));

        public void Dispose() => _registration.Dispose();
    }
}

internal sealed class TestServices(IServiceCollection collection) : IPluginServices
{
    public IServiceCollection Collection { get; } = collection;

    public IServiceProvider? Provider { get; set; }
}

internal sealed class TestContributionRegistry(IServiceCollection services) : IContributionRegistry
{
    public List<(string Type, string Id)> Registrations { get; } = [];

    public void AddPromptFragmentProvider<TProvider>(string id)
        where TProvider : class, IPromptFragmentProvider
    {
        Registrations.Add(("prompt.fragment", id));
        services.TryAddSingleton<TProvider>();
    }

    public void AddChatUpstream<TUpstream>(string id) where TUpstream : class, IChatUpstream
        => throw new NotSupportedException();

    public void AddChatResponder<TResponder>(string id, int priority = 0) where TResponder : class, IChatResponder
        => throw new NotSupportedException();

    public void AddChatEventSubscriber<TSubscriber>(string id) where TSubscriber : class, IChatEventSubscriber
        => throw new NotSupportedException();

    public List<Natter.Cli.Abstractions.CliCommandDescriptor> Commands { get; } = [];

    public void AddCliCommand(Natter.Cli.Abstractions.CliCommandDescriptor descriptor)
    {
        Commands.Add(descriptor);
        Registrations.Add(("cli.command", descriptor.Name.Replace(' ', '.')));
        services.TryAddSingleton(descriptor.HandlerFactory);
    }

    public void AddPortAdapter<TService, TImplementation>(string id, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class
        where TImplementation : class, TService
        => throw new NotSupportedException();

    public void AddPromptProvider<TProvider>(string id) where TProvider : class, IPromptProvider
        => throw new NotSupportedException();

    public void AddContextAssembler<TAssembler>(string id) where TAssembler : class, IContextAssembler
        => throw new NotSupportedException();

    public void AddMemoryStore<TStore>(string id) where TStore : class, IMemoryEntryStore
        => throw new NotSupportedException();
}

internal sealed class TestDependencies : IPluginDependencies
{
    public bool IsLoaded(string pluginId) => false;

    public string? GetVersion(string pluginId) => null;
}

internal sealed class TestPluginContext : IPluginContext
{
    private readonly ServiceCollection _services = new();

    public TestPluginContext(string configJson)
    {
        Config = new TestPluginConfig(configJson);
        Services = new TestServices(_services);
        Contributions = new TestContributionRegistry(_services);
    }

    public PluginManifest Manifest { get; } = StateVitalsPlugin.Manifest;

    public IPluginConfig Config { get; }

    public IPluginSecrets Secrets { get; } = new TestPluginSecrets();

    public IPluginLog Log { get; } = new TestPluginLog();

    public IPluginLifetime Lifetime { get; } = new TestLifetime();

    public IPluginServices Services { get; }

    public IContributionRegistry Contributions { get; }

    public IPluginDependencies Dependencies { get; } = new TestDependencies();

    /// <summary>Resolves a service from the plugin-local collection (mirrors the host's provider).</summary>
    public T Resolve<T>() where T : notnull => _services.BuildServiceProvider().GetRequiredService<T>();
}

internal static class TestPluginContextExtensions
{
    public static IEnumerable<(string Type, string Id)> Registrations(this TestPluginContext context)
        => ((TestContributionRegistry)context.Contributions).Registrations;
}

/// <summary>Disposable temp directory used by the state-directory and staging tests.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "natter-state-vitals-plugin-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Locates the staged plugin package (plugin.json + schema + definitions) in the test output.</summary>
internal static class PluginPackage
{
    public static string Directory =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "plugin-package");

    public static string Read(string fileName)
        => File.ReadAllText(System.IO.Path.Combine(Directory, fileName));
}
