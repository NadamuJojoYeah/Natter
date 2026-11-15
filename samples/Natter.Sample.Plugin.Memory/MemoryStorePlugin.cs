using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Sample.Plugin.Memory;

/// <summary>Demonstrates the NPS memory.store contract with an isolated in-memory store.</summary>
public sealed class MemoryStorePlugin : INatterPlugin
{
    public static PluginManifest Manifest { get; } = PluginManifestReader.ReadEmbedded(
        typeof(MemoryStorePlugin).Assembly,
        "Natter.Sample.Plugin.Memory.plugin.json");

    public ValueTask ConfigureAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
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

    public ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(request, cancellationToken);

    public ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default)
        => _inner.ExportAsync(request, cancellationToken);

    public ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default)
        => _inner.ImportAsync(request, cancellationToken);
}
