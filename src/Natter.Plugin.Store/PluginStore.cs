using System.Globalization;
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
    private readonly List<string> _warnings = [];

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

    /// <summary>Recoverable problems observed by the store (corrupt state, failed rollbacks).</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings)
                return _warnings.ToArray();
        }
    }

    public string PendingPath => Path.Combine(RootDirectory, "pending.json");

    public string GetPluginConfigPath(string pluginId)
        => Path.Combine(ConfigDirectory, PluginStoreIds.Validate(pluginId) + ".json");

    public string GetPluginDataDirectory(string pluginId)
        => Path.Combine(DataDirectory, PluginStoreIds.Validate(pluginId));

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

    /// <summary>
    /// Filesystem roots for enabled plugins (compatibility view). Prefer
    /// <see cref="GetActivePluginCandidates"/>: a bare directory string loses the stored trust and
    /// the startup integrity result, which is exactly how S1-12 happened.
    /// </summary>
    public IReadOnlyList<string> GetActivePluginDirectories()
        => GetActivePluginCandidates().Select(candidate => candidate.Directory).ToArray();

    /// <summary>
    /// Activation candidates with the trust decision recorded at install time and a fresh integrity
    /// check (re-hashed against <c>package.sha256</c>). The host policy consumes the trust/integrity
    /// fields; a tampered directory is reported as <see cref="InstalledPluginIntegrity.Tampered"/>.
    /// </summary>
    public IReadOnlyList<InstalledPluginCandidate> GetActivePluginCandidates()
    {
        _gate.Wait();
        try
        {
            var state = ReadState();
            var candidates = new List<InstalledPluginCandidate>();

            foreach (var record in state.Plugins.Where(plugin => plugin.Enabled))
            {
                var active = Path.Combine(RootDirectory, record.Id, record.ActiveVersion);
                if (Directory.Exists(active))
                {
                    candidates.Add(InstalledPluginIntegrityCodes.Candidate(
                        record.Id, record.ActiveVersion, active, record.Trust, record.SignerKeyId));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(record.PreviousVersion))
                {
                    var previous = Path.Combine(RootDirectory, record.Id, record.PreviousVersion);
                    if (Directory.Exists(previous))
                    {
                        candidates.Add(InstalledPluginIntegrityCodes.Candidate(
                            record.Id, record.PreviousVersion!, previous, record.Trust, record.SignerKeyId));
                    }
                }
            }

            return candidates;
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

            foreach (var warning in validation.WarningList)
                AddWarning($"{warning.Code}: {warning.Message}");

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

            // S2-C5: intent first, then move, then commit. Every step has a compensating action so a
            // failure never leaves installed.json pointing at a directory that does not exist.
            var now = _clock();
            var previousPending = pending;
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

            pending.Plugins.RemoveAll(plugin => string.Equals(plugin.PluginId, manifest.Id, StringComparison.Ordinal));
            pending.Plugins.Add(new PendingActivationRecord
            {
                PluginId = manifest.Id,
                Version = manifest.Version,
                PreviousVersion = record.PreviousVersion,
                CreatedAt = now,
            });

            // 1) Intent: an interrupted install stays reconcilable from pending.json.
            WritePending(pending);

            Directory.CreateDirectory(Path.Combine(RootDirectory, manifest.Id));
            destination = versionDirectory;

            var moved = false;
            var committed = false;
            try
            {
                // 2) Publish the payload.
                Directory.Move(extract, destination);
                moved = true;

                // 3) Commit the record only after the files exist.
                state.Plugins.RemoveAll(plugin => string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal));
                state.Plugins.Add(record);
                WriteState(state);
                committed = true;
            }
            catch
            {
                if (!committed)
                    RollbackStateCommit(manifest.Id);

                if (moved)
                {
                    TryDeleteDirectory(destination);
                    destination = null;
                }

                TryRestorePending(previousPending);
                throw;
            }

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
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        // S1-01: the id is validated before any path is derived from it. Absolute paths, "..",
        // uppercase and over-long ids are rejected by the store, not by the CLI only.
        var id = PluginStoreIds.Validate(pluginId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = ReadState();
            var pending = ReadPending();
            var record = state.Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, id, StringComparison.Ordinal));

            if (record is not null)
            {
                var trash = Path.Combine(TrashDirectory, "txn-" + Guid.NewGuid().ToString("N"));
                var pluginRoot = Path.Combine(RootDirectory, id);

                if (Directory.Exists(pluginRoot))
                {
                    Directory.CreateDirectory(trash);
                    Directory.Move(pluginRoot, Path.Combine(trash, id));
                }

                state.Plugins.Remove(record);
                WriteState(state);
            }

            pending.Plugins.RemoveAll(item => string.Equals(item.PluginId, id, StringComparison.Ordinal));
            WritePending(pending);

            // Data/config are only removed for an installed plugin, or when the operator explicitly
            // passes --force. An unknown id with --delete-data must not touch the filesystem.
            var allowDeletes = record is not null || force;
            if (deleteConfig && allowDeletes)
                TryDeleteFile(EnsureWithinDirectory(ConfigDirectory, GetPluginConfigPath(id)));

            if (deleteData && allowDeletes)
                TryDeleteDirectory(EnsureWithinDirectory(DataDirectory, GetPluginDataDirectory(id)));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Clears staging/trash under the same in-process gate as install plus an exclusive cross-process
    /// lock file, so a concurrent install in another process cannot lose its staging directory (S2-C10).
    /// </summary>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var storeLock = AcquireStoreLock(cancellationToken);
            ClearDirectory(StagingDirectory);
            ClearDirectory(TrashDirectory);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Exclusive cross-process lock on the store root (short retry window).</summary>
    private FileStream AcquireStoreLock(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(RootDirectory, ".store.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    /// Reads <c>installed.json</c>. Corruption never bricks the store (S2-C15): the broken document is
    /// preserved as <c>installed.json.corrupt-&lt;utc&gt;</c>, individually readable records are
    /// salvaged, the repaired document is written back and a warning is recorded.
    /// </summary>
    private PluginStoreState ReadState()
    {
        if (!File.Exists(StatePath))
            return new PluginStoreState();

        var text = File.ReadAllText(StatePath);
        try
        {
            return JsonSerializer.Deserialize<PluginStoreState>(text, SerializerOptions) ?? new PluginStoreState();
        }
        catch (JsonException exception)
        {
            return RecoverState(text, exception);
        }
    }

    private PluginStoreState RecoverState(string corruptText, JsonException exception)
    {
        var stamp = _clock().ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var backup = StatePath + ".corrupt-" + stamp;
        try
        {
            File.WriteAllText(backup, corruptText, new UTF8Encoding(false));
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            backup = "backup-failed:" + writeError.GetType().Name;
        }

        var salvaged = new PluginStoreState();
        var skipped = 0;

        // Element-wise salvage: a truncated document is still a valid JsonException, but individual
        // records before the damage are usually intact. Scan the "plugins" array and deserialize each
        // complete object, so one bad record does not discard the rest (S2-C15).
        foreach (var element in ExtractCompleteObjects(corruptText))
        {
            try
            {
                var record = JsonSerializer.Deserialize<InstalledPluginRecord>(element, SerializerOptions);
                if (record is not null && !string.IsNullOrWhiteSpace(record.Id))
                    salvaged.Plugins.Add(record);
                else
                    skipped++;
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        AddWarning($"installed.json was corrupt ({exception.Message}); salvaged {salvaged.Plugins.Count} record(s)"
                   + (skipped > 0 ? $", skipped {skipped}" : string.Empty)
                   + $", copy at '{backup}'.");

        try
        {
            if (!backup.StartsWith("backup-", StringComparison.Ordinal))
                WriteState(salvaged);
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            AddWarning($"repaired installed.json could not be persisted ({writeError.GetType().Name}).");
        }

        return salvaged;
    }

    /// <summary>
    /// Returns the text of every complete <c>{...}</c> object inside the first array after
    /// <c>"plugins"</c>. String literals and escapes are honoured so braces inside strings do not
    /// confuse the scan; a truncated trailing object is simply dropped.
    /// </summary>
    internal static IReadOnlyList<string> ExtractCompleteObjects(string json)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(json))
            return results;

        var arrayStart = FindPluginsArrayStart(json);
        if (arrayStart < 0)
            return results;

        var depth = 0;
        var inString = false;
        var escaped = false;
        var objectStart = -1;

        for (var index = arrayStart + 1; index < json.Length; index++)
        {
            var character = json[index];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0)
                        objectStart = index;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        results.Add(json[objectStart..(index + 1)]);
                        objectStart = -1;
                    }

                    break;
                case ']' when depth == 0:
                    return results;
            }
        }

        return results;
    }

    private static int FindPluginsArrayStart(string json)
    {
        var marker = json.IndexOf("\"plugins\"", StringComparison.Ordinal);
        if (marker < 0)
            return -1;

        var bracket = json.IndexOf('[', marker);
        var colon = json.IndexOf(':', marker);
        return bracket > 0 && colon > 0 && bracket > colon ? bracket : -1;
    }

    private void RollbackStateCommit(string pluginId)
    {
        try
        {
            if (!File.Exists(StatePath))
                return;

            var onDisk = ReadState();
            if (onDisk.Plugins.RemoveAll(plugin => string.Equals(plugin.Id, pluginId, StringComparison.Ordinal)) > 0)
                WriteState(onDisk);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PluginStoreException)
        {
            AddWarning($"install rollback could not restore installed.json ({exception.GetType().Name}).");
        }
    }

    private void TryRestorePending(PendingActivationState previous)
    {
        try
        {
            WritePending(previous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddWarning($"install rollback could not restore pending.json ({exception.GetType().Name}).");
        }
    }

    private void AddWarning(string warning)
    {
        lock (_warnings)
            _warnings.Add(warning);
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

    /// <summary>Full-path + root-prefix guard (with separator) for every delete target.</summary>
    private static string EnsureWithinDirectory(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candidateFull = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(rootFull) + Path.DirectorySeparatorChar;
        if (!candidateFull.StartsWith(prefix, comparison))
        {
            throw new PluginStoreException(
                $"refusing to delete outside the plugin store root: '{candidateFull}'",
                [new PluginPackageIssue("delete-outside-store", $"Delete target '{candidateFull}' is outside '{rootFull}'.")]);
        }

        return candidateFull;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            // A symlinked data directory deletes the link only; the link target is never removed.
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                Directory.Delete(directory, recursive: false);
                return;
            }

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
