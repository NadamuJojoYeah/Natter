using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Natter.Cli.Abstractions;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.PromptLibrary.Tests;

internal sealed class TestLog : IPluginLog
{
    public List<string> Messages { get; } = [];

    public void Debug(string message, params object?[] args) => Messages.Add("debug: " + Format(message, args));

    public void Information(string message, params object?[] args) => Messages.Add("info: " + Format(message, args));

    public void Warning(string message, params object?[] args) => Messages.Add("warn: " + Format(message, args));

    public void Error(Exception? exception, string message, params object?[] args)
        => Messages.Add("error: " + Format(message, args) + (exception is null ? string.Empty : " " + exception.Message));

    /// <summary>Mimics Microsoft.Extensions.Logging named-placeholder rendering; no string.Format.</summary>
    private static string Format(string message, object?[] args)
    {
        if (args.Length == 0)
            return message;

        var builder = new StringBuilder(message.Length + 32);
        var argument = 0;
        for (var index = 0; index < message.Length; index++)
        {
            if (message[index] == '{' && argument < args.Length)
            {
                var close = message.IndexOf('}', index + 1);
                if (close > index)
                {
                    builder.Append(args[argument++]?.ToString() ?? string.Empty);
                    index = close;
                    continue;
                }
            }

            builder.Append(message[index]);
        }

        return builder.ToString();
    }
}

internal sealed class TestPluginConfig : IPluginConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly JsonElement _raw;

    public TestPluginConfig(string json)
    {
        using var document = JsonDocument.Parse(json);
        _raw = document.RootElement.Clone();
    }

    public JsonElement Raw => _raw;

    public T Get<T>() where T : class
        => TryGet<T>(out var value) && value is not null
            ? value
            : throw new PluginConfigException($"Config is not a valid {typeof(T).Name}.");

    public bool TryGet<T>(out T? value) where T : class
    {
        try
        {
            value = _raw.Deserialize<T>(SerializerOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    public bool TryGetSecretReference(string jsonPointer, out SecretReference? reference)
    {
        reference = null;
        return false;
    }
}

internal sealed class TestSecrets : IPluginSecrets
{
    public string GetRequired(string name) => throw new PluginSecretException($"Unexpected secret '{name}'.");

    public string? Get(string name) => null;
}

internal sealed class TestLifetime : IPluginLifetime
{
    public CancellationToken Stopping => CancellationToken.None;

    public IDisposable OnStop(Func<CancellationToken, ValueTask> callback) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class TestServices(IServiceCollection collection) : IPluginServices
{
    public IServiceCollection Collection { get; } = collection;

    public IServiceProvider? Provider { get; set; }
}

internal sealed class TestDependencies : IPluginDependencies
{
    public bool IsLoaded(string pluginId) => false;

    public string? GetVersion(string pluginId) => null;
}

internal sealed class TestContributionRegistry(IServiceCollection services) : IContributionRegistry
{
    public List<(string Type, string Id)> Registrations { get; } = [];

    public List<CliCommandDescriptor> Commands { get; } = [];

    public void AddChatUpstream<TUpstream>(string id) where TUpstream : class, Natter.Chat.IChatUpstream
        => throw new NotSupportedException();

    public void AddChatResponder<TResponder>(string id, int priority = 0) where TResponder : class, Natter.Application.Chat.IChatResponder
        => throw new NotSupportedException();

    public void AddChatEventSubscriber<TSubscriber>(string id) where TSubscriber : class, Natter.Chat.IChatEventSubscriber
        => throw new NotSupportedException();

    public void AddCliCommand(CliCommandDescriptor descriptor)
    {
        Commands.Add(descriptor);
        Registrations.Add(("cli.command", descriptor.Name.Trim().Replace(' ', '.')));
    }

    public void AddPortAdapter<TService, TImplementation>(string id, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TService : class
        where TImplementation : class, TService
        => throw new NotSupportedException();

    public void AddPromptProvider<TProvider>(string id) where TProvider : class, Natter.Application.Chat.IPromptProvider
    {
        Registrations.Add(("prompt.provider", id));
        services.TryAddSingleton<TProvider>();
    }

    public void AddContextAssembler<TAssembler>(string id) where TAssembler : class, Natter.Application.Chat.IContextAssembler
        => throw new NotSupportedException();

    public void AddMemoryStore<TStore>(string id) where TStore : class, Natter.Application.Chat.IMemoryStore
        => throw new NotSupportedException();
}

internal sealed class TestPluginContext : IPluginContext
{
    public required PluginManifest Manifest { get; init; }

    public IPluginConfig Config { get; init; } = new TestPluginConfig("{}");

    public IPluginSecrets Secrets { get; init; } = new TestSecrets();

    public IPluginLog Log { get; init; } = new TestLog();

    public IPluginLifetime Lifetime { get; init; } = new TestLifetime();

    public IPluginServices Services { get; init; } = new TestServices(new ServiceCollection());

    public IContributionRegistry Contributions { get; init; } = new TestContributionRegistry(new ServiceCollection());

    public IPluginDependencies Dependencies { get; init; } = new TestDependencies();
}

internal sealed class TestConsole : IConsole
{
    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    TextWriter IConsole.Out => Out;

    TextWriter IConsole.Error => Error;

    public bool IsInteractive => false;

    public string? ReadLine() => null;
}

internal sealed class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public TestServiceProvider Add<TService>(TService service) where TService : class
    {
        _services[typeof(TService)] = service;
        return this;
    }

    public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var service) ? service : null;
}

/// <summary>Disposable temp directory used by loader/path tests.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "natter-prompt-library-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "prompts"));
    }

    public string Path { get; }

    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    public void Dispose()
    {
        try
        {
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
