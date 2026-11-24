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

/// <summary>
/// Sample <c>memory.store</c> (P6.7 contract): a JSON-file backed wrapper around the in-memory
/// five-kind store. The persisted document is the versioned v2 envelope, so an old v1 file is
/// upgraded on load via <see cref="MemoryExportBridge"/> and a newer file is refused rather than
/// silently truncated.
/// </summary>
public sealed class SampleMemoryStore : IMemoryEntryStore
{
    private readonly SampleMemoryEntryStore _inner = new();
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

    public ValueTask<MemoryEntry> WriteAsync(MemoryEntryWriteRequest request, CancellationToken cancellationToken = default)
    {
        var write = _inner.WriteAsync(request, cancellationToken);
        Persist();
        return write;
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryEntryQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public ValueTask<MemoryEntry?> GetAsync(Source source, Conversation conversation, User user, string id, CancellationToken cancellationToken = default)
        => _inner.GetAsync(source, conversation, user, id, cancellationToken);

    public ValueTask<MemoryArchiveResult> ArchiveAsync(Source source, Conversation conversation, User user, string id, CancellationToken cancellationToken = default)
    {
        var archive = _inner.ArchiveAsync(source, conversation, user, id, cancellationToken);
        Persist();
        return archive;
    }

    public ValueTask ReinforceRecallAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var reinforce = _inner.ReinforceRecallAsync(ids, cancellationToken);
        Persist();
        return reinforce;
    }

    public ValueTask<MemoryEntryExport> ExportAsync(Source source, Conversation conversation, User user, bool includePrivate, CancellationToken cancellationToken = default)
        => _inner.ExportAsync(source, conversation, user, includePrivate, cancellationToken);

    public async ValueTask<int> ImportAsync(Source source, Conversation conversation, User user, MemoryEntryExport export, CancellationToken cancellationToken = default)
    {
        var count = await _inner.ImportAsync(source, conversation, user, export, cancellationToken).ConfigureAwait(false);
        Persist();
        return count;
    }

    /// <summary>Whole-store snapshot for persistence (scope independent).</summary>
    public MemoryEntryExport Snapshot() => MemoryEntryExport.Create(_inner.Snapshot(), DateTimeOffset.UtcNow);

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            var text = File.ReadAllText(_path);
            MemoryEntryExport? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<MemoryEntryExport>(text, JsonOptions);
            }
            catch (JsonException)
            {
                // A v1 file has no `items[].type`, so fall back to the legacy envelope and bridge it.
                var legacy = JsonSerializer.Deserialize<MemoryExport>(text, JsonOptions);
                if (legacy is not null)
                    envelope = MemoryExportBridge.Upgrade(legacy);
            }

            if (envelope is not null)
                _inner.Restore(envelope.EnsureReadable().Items);
        }
        catch (Exception exception) when (exception is JsonException or MemoryExportVersionException or InvalidOperationException)
        {
            // A damaged or too-new file must not stop the host: start empty and keep the file intact
            // so the operator can inspect or downgrade it.
        }
    }

    private void Persist()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(Snapshot(), JsonOptions));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
