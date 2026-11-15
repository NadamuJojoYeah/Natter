using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.Memory;

/// <summary>Demonstrates the NPS memory.store contract with a small JSON-file store.</summary>
public sealed class MemoryStorePlugin : INatterPlugin
{
    private readonly string? _storePath;

    public MemoryStorePlugin()
    {
    }

    /// <summary>Test/embedded seam; production uses the configured path.</summary>
    public MemoryStorePlugin(string storePath) => _storePath = storePath;

    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(MemoryStorePlugin).Assembly,
        "Natter.Sample.Plugin.Memory.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        var path = _storePath
                   ?? Environment.GetEnvironmentVariable("NATTER_SAMPLE_MEMORY_PATH")
                   ?? Path.Combine(Path.GetTempPath(), "natter-sample-memory.json");
        context.Services.Collection.AddSingleton(new SampleMemoryStore(path));
        context.Contributions.AddMemoryStore<SampleMemoryStore>("file");
        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DeactivateAsync(IPluginContext context, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed class SampleMemoryStore : IMemoryStore
{
    private readonly InMemoryMemoryStore _inner = new();
    private readonly string _path;
    private readonly Lock _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public SampleMemoryStore(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        var item = _inner.WriteAsync(request, cancellationToken);
        Persist();
        return item;
    }

    public ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var deleted = _inner.DeleteAsync(request, cancellationToken);
        Persist();
        return deleted;
    }

    public async ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default)
    {
        var export = await _inner.ExportAsync(request, cancellationToken).ConfigureAwait(false);
        Persist();
        return export;
    }

    public ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default)
    {
        var count = _inner.ImportAsync(request, cancellationToken);
        Persist();
        return count;
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var export = JsonSerializer.Deserialize<MemoryExport>(File.ReadAllText(_path), JsonOptions);
            if (export is { SchemaVersion: 1 })
                _inner.ImportAsync(new MemoryImportRequest(default!, default!, default!, export, Overwrite: true)).AsTask().GetAwaiter().GetResult();
        }
        catch (JsonException)
        {
        }
    }

    private void Persist()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var export = _inner.ExportAll();
            File.WriteAllText(_path, JsonSerializer.Serialize(export, JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
