using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Plugin.Store;

/// <summary>
/// App-private NPS plugin store. Layout follows the Android plugin host design:
/// <c>plugins/&lt;id&gt;/&lt;version&gt;</c>, <c>.staging</c>, <c>.trash</c>,
/// <c>installed.json</c> and <c>pending.json</c>.
/// </summary>
public sealed class PluginStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly PluginStoreOptions _options;
    private readonly PluginPackageValidator _validator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;

    public PluginStore(PluginStoreOptions options, PluginPackageValidator? validator = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.RootDirectory))
            throw new ArgumentException("Plugin store root directory must not be empty.", nameof(options));

        RootDirectory = Path.GetFullPath(options.RootDirectory);
        _validator = validator ?? new PluginPackageValidator(options);
        _clock = options.Clock ?? (() => DateTimeOffset.UtcNow);

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(StagingDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    public string RootDirectory { get; }

    public string StagingDirectory => Path.Combine(RootDirectory, ".staging");

    public string TrashDirectory => Path.Combine(RootDirectory, ".trash");

    public string ConfigDirectory => Path.GetFullPath(Path.Combine(RootDirectory, "..", "plugin-config"));

    public string DataDirectory => Path.GetFullPath(Path.Combine(RootDirectory, "..", "plugin-data"));

    public string StatePath => Path.Combine(RootDirectory, "installed.json");

    public string PendingPath => Path.Combine(RootDirectory, "pending.json");

    public string GetPluginConfigPath(string pluginId)
        => Path.Combine(ConfigDirectory, pluginId + ".json");

    public string GetPluginDataDirectory(string pluginId)
        => Path.Combine(DataDirectory, pluginId);

    public IReadOnlyList<InstalledPluginRecord> GetInstalledPlugins()
    {
        _gate.Wait();
        try
        {
            return ReadState().Plugins
                .OrderBy(plugin => plugin.Id, StringComparer.Ordinal)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Filesystem roots for enabled plugins; pass these to the NPS file loader.</summary>
    public IReadOnlyList<string> GetActivePluginDirectories()
    {
        _gate.Wait();
        try
        {
            var state = ReadState();
            var directories = new List<string>();

            foreach (var record in state.Plugins.Where(plugin => plugin.Enabled))
            {
                var active = Path.Combine(RootDirectory, record.Id, record.ActiveVersion);
                if (Directory.Exists(active))
                {
                    directories.Add(active);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(record.PreviousVersion))
                {
                    var previous = Path.Combine(RootDirectory, record.Id, record.PreviousVersion);
                    if (Directory.Exists(previous))
                        directories.Add(previous);
                }
            }

            return directories;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginStoreInstallResult> InstallAsync(
        string packagePath,
        bool allowUpdate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transaction = "txn-" + Guid.NewGuid().ToString("N");
        var staging = Path.Combine(StagingDirectory, transaction);
        var extract = Path.Combine(staging, "extract");
        string? destination = null;

        try
        {
            var state = ReadState();
            var pending = ReadPending();
            var existing = state.Plugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Id, ExtractIdHint(packagePath), StringComparison.Ordinal));

            var installedVersions = state.Plugins
                .Select(plugin => new InstalledPluginVersion(plugin.Id, plugin.ActiveVersion, plugin.Enabled))
                .ToArray();

            Directory.CreateDirectory(staging);
            var validation = await _validator.ValidateAsync(
                packagePath,
                extract,
                installedVersions,
                cancellationToken).ConfigureAwait(false);

            if (!validation.IsValid || validation.Package is null)
            {
                throw new PluginStoreException(
                    "Plugin package validation failed.",
                    validation.Issues);
            }

            var package = validation.Package;
            var manifest = package.Manifest;

            existing = state.Plugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal));

            if (existing is not null && !allowUpdate)
                throw new PluginStoreException($"Plugin '{manifest.Id}' is already installed.", []);

            var versionDirectory = Path.Combine(RootDirectory, manifest.Id, manifest.Version);
            if (Directory.Exists(versionDirectory))
            {
                throw new PluginStoreException(
                    $"Plugin '{manifest.Id}' version '{manifest.Version}' is already installed; version directories are immutable.",
                    []);
            }

            Directory.CreateDirectory(Path.Combine(RootDirectory, manifest.Id));
            destination = versionDirectory;
            Directory.Move(extract, destination);

            var now = _clock();
            var record = new InstalledPluginRecord
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                ActiveVersion = manifest.Version,
                PreviousVersion = existing?.ActiveVersion,
                Enabled = existing?.Enabled ?? true,
                Trust = package.SignerKeyId is not null ? "signed" : "unsigned",
                SignerKeyId = package.SignerKeyId,
                Permissions = manifest.Permissions?.ToList() ?? [],
                InstalledAt = now,
                LastActivatedAt = null,
                FailureReason = null,
                LastKnownState = PluginState.Discovered,
            };

            state.Plugins.RemoveAll(plugin => string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal));
            state.Plugins.Add(record);

            pending.Plugins.RemoveAll(plugin => string.Equals(plugin.PluginId, manifest.Id, StringComparison.Ordinal));
            pending.Plugins.Add(new PendingActivationRecord
            {
                PluginId = manifest.Id,
                Version = manifest.Version,
                PreviousVersion = record.PreviousVersion,
                CreatedAt = now,
            });

            WriteState(state);
            WritePending(pending);
            destination = null;

            var movedPackage = package with { ExtractDirectory = versionDirectory };
            return new PluginStoreInstallResult(record, movedPackage, existing is not null);
        }
        finally
        {
            if (destination is not null)
                TryDeleteDirectory(destination);

            TryDeleteDirectory(staging);
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal))
                         ?? throw new PluginStoreException($"Plugin '{pluginId}' is not installed.", []);

            record.Enabled = enabled;
            record.LastKnownState = enabled ? PluginState.Discovered : PluginState.Disabled;
            WriteState(state);

            if (!enabled)
            {
                var pending = ReadPending();
                pending.Plugins.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
                WritePending(pending);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginStoreRollbackResult> RollbackAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var pending = ReadPending();
            var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

            if (record is null)
                return new PluginStoreRollbackResult(pluginId, false, null, "Plugin is not installed.");

            var previous = record.PreviousVersion;
            if (string.IsNullOrWhiteSpace(previous)
                || !Directory.Exists(Path.Combine(RootDirectory, pluginId, previous)))
            {
                record.Enabled = false;
                record.FailureReason = "No previous version is available.";
                record.LastKnownState = PluginState.Failed;
                WriteState(state);
                pending.Plugins.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
                WritePending(pending);
                return new PluginStoreRollbackResult(pluginId, false, record.ActiveVersion, record.FailureReason);
            }

            record.ActiveVersion = previous;
            record.PreviousVersion = null;
            record.FailureReason = "Rolled back after activation failure.";
            record.LastKnownState = PluginState.Stopped;
            WriteState(state);

            pending.Plugins.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
            WritePending(pending);

            return new PluginStoreRollbackResult(pluginId, true, record.ActiveVersion, record.FailureReason);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConfirmActivationAsync(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var pending = ReadPending();
            var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

            if (record is not null && string.Equals(record.ActiveVersion, version, StringComparison.Ordinal))
            {
                record.FailureReason = null;
                record.LastActivatedAt = _clock();
                record.LastKnownState = PluginState.Activated;
                WriteState(state);
            }

            pending.Plugins.RemoveAll(item =>
                string.Equals(item.PluginId, pluginId, StringComparison.Ordinal)
                && string.Equals(item.Version, version, StringComparison.Ordinal));
            WritePending(pending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginStoreReconcileResult> ReconcileAsync(
        IPluginRuntimeDiagnostics diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = ReadPending();
            var confirmed = new List<string>();
            var rolledBack = new List<string>();
            var stillPending = new List<string>();

            foreach (var candidate in pending.Plugins.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var node = diagnostics.Graph.FirstOrDefault(item =>
                    string.Equals(item.Id, candidate.PluginId, StringComparison.Ordinal));

                if (node is null)
                {
                    stillPending.Add(candidate.PluginId);
                    continue;
                }

                if (node.State == PluginState.Activated)
                {
                    confirmed.Add(candidate.PluginId);
                    pending.Plugins.Remove(candidate);
                    continue;
                }

                if (node.State is PluginState.Failed or PluginState.Blocked or PluginState.Disabled or PluginState.Skipped)
                {
                    rolledBack.Add(candidate.PluginId);
                    pending.Plugins.Remove(candidate);
                    continue;
                }

                stillPending.Add(candidate.PluginId);
            }

            WritePending(pending);

            foreach (var pluginId in confirmed)
            {
                var state = ReadState();
                var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
                if (record is not null)
                {
                    record.FailureReason = null;
                    record.LastActivatedAt = _clock();
                    record.LastKnownState = PluginState.Activated;
                    WriteState(state);
                }
            }

            foreach (var pluginId in rolledBack)
            {
                var state = ReadState();
                var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
                if (record is null)
                    continue;

                var previous = record.PreviousVersion;
                if (!string.IsNullOrWhiteSpace(previous)
                    && Directory.Exists(Path.Combine(RootDirectory, pluginId, previous)))
                {
                    record.ActiveVersion = previous;
                    record.PreviousVersion = null;
                    record.FailureReason = "Activation failed; previous version restored.";
                    record.LastKnownState = PluginState.Stopped;
                    WriteState(state);
                }
                else
                {
                    record.Enabled = false;
                    record.FailureReason = "Activation failed and no previous version is available.";
                    record.LastKnownState = PluginState.Failed;
                    WriteState(state);
                }
            }

            return new PluginStoreReconcileResult(confirmed, rolledBack, stillPending);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UninstallAsync(
        string pluginId,
        bool deleteData = false,
        bool deleteConfig = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var pending = ReadPending();
            var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));

            if (record is not null)
            {
                var trash = Path.Combine(TrashDirectory, "txn-" + Guid.NewGuid().ToString("N"));
                var pluginRoot = Path.Combine(RootDirectory, pluginId);

                if (Directory.Exists(pluginRoot))
                {
                    Directory.CreateDirectory(trash);
                    Directory.Move(pluginRoot, Path.Combine(trash, pluginId));
                }

                state.Plugins.Remove(record);
                WriteState(state);
            }

            pending.Plugins.RemoveAll(item => string.Equals(item.PluginId, pluginId, StringComparison.Ordinal));
            WritePending(pending);

            if (deleteConfig)
                TryDeleteFile(GetPluginConfigPath(pluginId));

            if (deleteData)
                TryDeleteDirectory(GetPluginDataDirectory(pluginId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClearDirectory(StagingDirectory);
        ClearDirectory(TrashDirectory);
        return Task.CompletedTask;
    }

    private PluginStoreState ReadState()
    {
        if (!File.Exists(StatePath))
            return new PluginStoreState();

        try
        {
            return JsonSerializer.Deserialize<PluginStoreState>(File.ReadAllText(StatePath), SerializerOptions)
                   ?? new PluginStoreState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new PluginStoreException($"Plugin store state '{StatePath}' is corrupt: {ex.Message}", ex);
        }
    }

    private PendingActivationState ReadPending()
    {
        if (!File.Exists(PendingPath))
            return new PendingActivationState();

        try
        {
            return JsonSerializer.Deserialize<PendingActivationState>(File.ReadAllText(PendingPath), SerializerOptions)
                   ?? new PendingActivationState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new PluginStoreException($"Plugin pending state '{PendingPath}' is corrupt: {ex.Message}", ex);
        }
    }

    private void WriteState(PluginStoreState state) => WriteJson(StatePath, state);

    private void WritePending(PendingActivationState pending) => WriteJson(PendingPath, pending);

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, SerializerOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string ExtractIdHint(string packagePath)
        => Path.GetFileNameWithoutExtension(packagePath);

    private static InstalledPluginRecord Clone(InstalledPluginRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Version = record.Version,
        ActiveVersion = record.ActiveVersion,
        PreviousVersion = record.PreviousVersion,
        Enabled = record.Enabled,
        Trust = record.Trust,
        SignerKeyId = record.SignerKeyId,
        Permissions = [.. record.Permissions],
        InstalledAt = record.InstalledAt,
        LastActivatedAt = record.LastActivatedAt,
        FailureReason = record.FailureReason,
        LastKnownState = record.LastKnownState,
    };

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch
            {
                // Cleanup is best-effort; a later startup retries.
            }
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
