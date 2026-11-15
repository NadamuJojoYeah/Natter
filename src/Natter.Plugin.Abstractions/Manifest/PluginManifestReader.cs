using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Natter.Plugin.Abstractions;

/// <summary>One manifest issue with a JSON-like path.</summary>
public sealed record PluginManifestIssue(string Path, string Code, string Message);

/// <summary>Result of reading plugin.json; <see cref="IsNps"/> is false for legacy manifests.</summary>
public sealed record PluginManifestReadResult(
    string Path,
    bool IsNps,
    PluginManifest? Manifest,
    IReadOnlyList<PluginManifestIssue> Errors,
    IReadOnlyList<PluginManifestIssue> Warnings)
{
    public bool IsValid => IsNps && Manifest is not null && Errors.Count == 0;
}

/// <summary>Reads and validates NPS v1 manifests. Legacy manifests are detected and left to the legacy loader.</summary>
public static partial class PluginManifestReader
{
    private const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PluginManifestReadResult Read(string pluginDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(pluginDirectory)
            ? string.Empty
            : Path.GetFullPath(pluginDirectory);
        var manifestPath = Path.Combine(directory, "plugin.json");

        if (!File.Exists(manifestPath))
        {
            return new PluginManifestReadResult(
                manifestPath,
                IsNps: false,
                Manifest: null,
                Errors: [],
                Warnings: []);
        }

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginManifestReadResult(
                manifestPath,
                IsNps: true,
                Manifest: null,
                Errors: [new PluginManifestIssue("$", "manifest-read", ex.Message)],
                Warnings: []);
        }

        return Parse(json, directory);
    }

    public static PluginManifestReadResult Parse(string json, string? pluginDirectory = null)
    {
        var errors = new List<PluginManifestIssue>();
        var warnings = new List<PluginManifestIssue>();
        var manifestPath = pluginDirectory is null ? "plugin.json" : Path.Combine(pluginDirectory, "plugin.json");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return new PluginManifestReadResult(
                manifestPath,
                IsNps: true,
                Manifest: null,
                Errors: [new PluginManifestIssue("$", "manifest-json", ex.Message)],
                Warnings: []);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PluginManifestReadResult(
                    manifestPath,
                    IsNps: true,
                    Manifest: null,
                    Errors: [new PluginManifestIssue("$", "manifest-shape", "Manifest must be a JSON object.")],
                    Warnings: []);
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaVersion))
            {
                // No schemaVersion means the legacy bootstrap manifest; the legacy loader owns it.
                return new PluginManifestReadResult(manifestPath, IsNps: false, Manifest: null, Errors: [], Warnings: []);
            }

            if (!root.TryGetProperty("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !IsKnownKind(kindElement.GetString()))
            {
                errors.Add(new PluginManifestIssue(
                    "/kind",
                    "invalid-kind",
                    "Kind is required and must be transport, feature or tool."));
            }

            if (schemaVersion.ValueKind != JsonValueKind.Number
                || !schemaVersion.TryGetInt32(out var version)
                || version != SupportedSchemaVersion)
            {
                errors.Add(new PluginManifestIssue(
                    "/schemaVersion",
                    "unsupported-schema",
                    $"Expected schemaVersion {SupportedSchemaVersion}."));
            }

            PluginManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(json, SerializerOptions);
            }
            catch (JsonException ex)
            {
                errors.Add(new PluginManifestIssue("$", "manifest-json", ex.Message));
                manifest = null;
            }

            if (manifest is null)
            {
                return new PluginManifestReadResult(manifestPath, IsNps: true, Manifest: null, Errors: errors, Warnings: warnings);
            }

            Validate(manifest, pluginDirectory, errors, warnings);
            return new PluginManifestReadResult(manifestPath, IsNps: true, Manifest: manifest, Errors: errors, Warnings: warnings);
        }
    }

    /// <summary>Validates an in-memory manifest (used for embedded plugins).</summary>
    public static (IReadOnlyList<PluginManifestIssue> Errors, IReadOnlyList<PluginManifestIssue> Warnings) ValidateManifest(
        PluginManifest manifest,
        string? pluginDirectory = null)
    {
        var errors = new List<PluginManifestIssue>();
        var warnings = new List<PluginManifestIssue>();
        Validate(manifest, pluginDirectory, errors, warnings);
        return (errors, warnings);
    }

    private static bool IsKnownKind(string? kind)
        => kind is not null
           && (string.Equals(kind, "transport", StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, "feature", StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase));

    private static void Validate(
        PluginManifest manifest,
        string? pluginDirectory,
        List<PluginManifestIssue> errors,
        List<PluginManifestIssue> warnings)
    {
        if (!IdPattern().IsMatch(manifest.Id))
            errors.Add(new PluginManifestIssue("/id", "invalid-id", "Plugin id must match ^[a-z0-9]+(?:[._-][a-z0-9]+)+$."));

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add(new PluginManifestIssue("/name", "required", "Plugin name is required."));

        if (!SemanticVersion.TryParse(manifest.Version, out _))
            errors.Add(new PluginManifestIssue("/version", "invalid-version", "Plugin version must be SemVer 2.0."));

        if (!Enum.IsDefined(manifest.Kind))
            errors.Add(new PluginManifestIssue("/kind", "invalid-kind", "Kind must be transport, feature or tool."));

        if (!VersionRange.TryParse(manifest.HostApi, out _))
            errors.Add(new PluginManifestIssue("/hostApi", "invalid-range", "hostApi must be a SemVer range."));

        ValidateEntry(manifest, pluginDirectory, errors);

        if (!string.IsNullOrWhiteSpace(manifest.ConfigSchema)
            && !IsSafeRelativePath(manifest.ConfigSchema!, pluginDirectory, out var schemaError))
        {
            errors.Add(new PluginManifestIssue("/configSchema", "invalid-path", schemaError!));
        }

        ValidateDependencies(manifest.DependsOn, "/dependsOn", required: true, errors);
        ValidateDependencies(manifest.OptionalDependsOn, "/optionalDependsOn", required: false, errors);
        ValidateConflicts(manifest.Conflicts, errors);
        ValidateOrderIds(manifest.LoadAfter, "/loadAfter", errors);
        ValidateOrderIds(manifest.LoadBefore, "/loadBefore", errors);
        ValidatePermissions(manifest, errors);
        ValidateSecrets(manifest, errors);
        ValidateSecretPermissions(manifest, errors);
        ValidateContributions(manifest, errors);
        ValidatePlatforms(manifest, errors);
        ValidateUnknownProperties(manifest, errors);

        if (pluginDirectory is not null && !string.IsNullOrWhiteSpace(manifest.Id))
        {
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(pluginDirectory));
            if (!string.IsNullOrEmpty(directoryName)
                && !string.Equals(directoryName, manifest.Id, StringComparison.Ordinal))
            {
                warnings.Add(new PluginManifestIssue(
                    "/id",
                    "directory-id-mismatch",
                    $"Directory '{directoryName}' does not match manifest id '{manifest.Id}'."));
            }
        }
    }

    private static void ValidateEntry(PluginManifest manifest, string? pluginDirectory, List<PluginManifestIssue> errors)
    {
        if (manifest.Entry is null)
        {
            errors.Add(new PluginManifestIssue("/entry", "required", "Entry is required."));
            return;
        }

        if (!IsSafeRelativePath(manifest.Entry.Assembly, pluginDirectory, out var error))
            errors.Add(new PluginManifestIssue("/entry/assembly", "invalid-path", error!));

        if (string.IsNullOrWhiteSpace(manifest.Entry.Type))
            errors.Add(new PluginManifestIssue("/entry/type", "required", "Entry type is required."));

        if (manifest.Entry.AdditionalProperties is { Count: > 0 } extra)
            AddUnknownPropertyErrors(extra, "/entry", errors);
    }

    private static void ValidateDependencies(
        List<PluginDependency>? dependencies,
        string path,
        bool required,
        List<PluginManifestIssue> errors)
    {
        if (dependencies is null)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < dependencies.Count; index++)
        {
            var dependency = dependencies[index];
            var itemPath = $"{path}/{index}";
            if (!IdPattern().IsMatch(dependency.Id))
                errors.Add(new PluginManifestIssue($"{itemPath}/id", "invalid-id", "Dependency id is invalid."));

            if (!seen.Add(dependency.Id))
                errors.Add(new PluginManifestIssue($"{itemPath}/id", "duplicate-dependency", $"Dependency '{dependency.Id}' is duplicated."));

            if (string.IsNullOrWhiteSpace(dependency.Version))
            {
                if (required)
                    errors.Add(new PluginManifestIssue($"{itemPath}/version", "required", "Required dependency needs a version range."));
            }
            else if (!VersionRange.TryParse(dependency.Version, out _))
            {
                errors.Add(new PluginManifestIssue($"{itemPath}/version", "invalid-range", "Dependency version must be a SemVer range."));
            }

            if (dependency.AdditionalProperties is { Count: > 0 } extra)
                AddUnknownPropertyErrors(extra, itemPath, errors);
        }
    }

    private static void ValidateConflicts(List<PluginConflict>? conflicts, List<PluginManifestIssue> errors)
    {
        if (conflicts is null)
            return;

        for (var index = 0; index < conflicts.Count; index++)
        {
            var conflict = conflicts[index];
            var itemPath = $"/conflicts/{index}";
            if (!IdPattern().IsMatch(conflict.Id))
                errors.Add(new PluginManifestIssue($"{itemPath}/id", "invalid-id", "Conflict id is invalid."));

            if (!string.IsNullOrWhiteSpace(conflict.Version) && !VersionRange.TryParse(conflict.Version, out _))
                errors.Add(new PluginManifestIssue($"{itemPath}/version", "invalid-range", "Conflict version must be a SemVer range."));

            if (conflict.AdditionalProperties is { Count: > 0 } extra)
                AddUnknownPropertyErrors(extra, itemPath, errors);
        }
    }

    private static void ValidateOrderIds(List<string>? ids, string path, List<PluginManifestIssue> errors)
    {
        if (ids is null)
            return;

        for (var index = 0; index < ids.Count; index++)
        {
            if (!IdPattern().IsMatch(ids[index]))
                errors.Add(new PluginManifestIssue($"{path}/{index}", "invalid-id", "Order target id is invalid."));
        }
    }

    private static void ValidatePermissions(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.Permissions is null)
            return;

        for (var index = 0; index < manifest.Permissions.Count; index++)
        {
            var permission = manifest.Permissions[index];
            if (!PluginPermissions.IsKnown(permission))
            {
                errors.Add(new PluginManifestIssue(
                    $"/permissions/{index}",
                    "unknown-permission",
                    $"Unknown permission '{permission}'."));
            }

            if (permission.StartsWith(PluginPermissions.SecretsPrefix, StringComparison.Ordinal))
            {
                var secretName = permission[PluginPermissions.SecretsPrefix.Length..];
                if (manifest.Secrets?.Any(secret => string.Equals(secret.Name, secretName, StringComparison.Ordinal)) != true)
                {
                    errors.Add(new PluginManifestIssue(
                        $"/permissions/{index}",
                        "undeclared-secret",
                        $"Permission '{permission}' does not match a declared secret."));
                }
            }
        }
    }

    private static void ValidateSecrets(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.Secrets is null)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < manifest.Secrets.Count; index++)
        {
            var secret = manifest.Secrets[index];
            var itemPath = $"/secrets/{index}";
            if (!SecretNamePattern().IsMatch(secret.Name))
                errors.Add(new PluginManifestIssue($"{itemPath}/name", "invalid-secret-name", "Secret name is invalid."));

            if (!seen.Add(secret.Name))
                errors.Add(new PluginManifestIssue($"{itemPath}/name", "duplicate-secret", $"Secret '{secret.Name}' is duplicated."));

            if (secret.AdditionalProperties is { Count: > 0 } extra)
                AddUnknownPropertyErrors(extra, itemPath, errors);
        }
    }

    private static void ValidateSecretPermissions(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.Secrets is null)
            return;

        var permissions = new HashSet<string>(manifest.Permissions ?? [], StringComparer.Ordinal);
        for (var index = 0; index < manifest.Secrets.Count; index++)
        {
            var secret = manifest.Secrets[index];
            if (!secret.Required)
                continue;

            var permission = PluginPermissions.Secret(secret.Name);
            if (!permissions.Contains(permission))
            {
                errors.Add(new PluginManifestIssue(
                    $"/secrets/{index}/name",
                    "missing-secret-permission",
                    $"Required secret '{secret.Name}' needs permission '{permission}'."));
            }
        }
    }

    private static void ValidateContributions(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.Contributions is null)
            return;

        var seen = new HashSet<(string Type, string Id)>();
        for (var index = 0; index < manifest.Contributions.Count; index++)
        {
            var contribution = manifest.Contributions[index];
            var itemPath = $"/contributions/{index}";
            if (!PluginContributionTypes.IsKnown(contribution.Type))
            {
                errors.Add(new PluginManifestIssue(
                    $"{itemPath}/type",
                    "unknown-contribution",
                    $"Unknown contribution type '{contribution.Type}'."));
            }

            if (string.IsNullOrWhiteSpace(contribution.Id))
                errors.Add(new PluginManifestIssue($"{itemPath}/id", "required", "Contribution id is required."));

            if (!seen.Add((contribution.Type, contribution.Id)))
            {
                errors.Add(new PluginManifestIssue(
                    $"{itemPath}/id",
                    "duplicate-contribution",
                    $"Contribution '{contribution.Type}/{contribution.Id}' is duplicated."));
            }

            if (contribution.AdditionalProperties is { Count: > 0 } extra)
                AddUnknownPropertyErrors(extra, itemPath, errors);
        }
    }

    private static void ValidatePlatforms(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.Platforms is null)
            return;

        string[] known = ["windows", "linux", "macos", "android"];
        for (var index = 0; index < manifest.Platforms.Count; index++)
        {
            if (!known.Contains(manifest.Platforms[index], StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new PluginManifestIssue(
                    $"/platforms/{index}",
                    "unknown-platform",
                    $"Unknown platform '{manifest.Platforms[index]}'."));
            }
        }
    }

    private static void ValidateUnknownProperties(PluginManifest manifest, List<PluginManifestIssue> errors)
    {
        if (manifest.AdditionalProperties is { Count: > 0 } extra)
            AddUnknownPropertyErrors(extra, string.Empty, errors);

        if (manifest.Entry?.AdditionalProperties is { Count: > 0 } entryExtra)
            AddUnknownPropertyErrors(entryExtra, "/entry", errors);
    }

    private static void AddUnknownPropertyErrors(
        IReadOnlyDictionary<string, JsonElement> properties,
        string basePath,
        List<PluginManifestIssue> errors)
    {
        foreach (var property in properties.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            errors.Add(new PluginManifestIssue(
                $"{basePath}/{EscapePointer(property)}",
                "unknown-property",
                $"Unknown manifest property '{property}'."));
        }
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0").Replace("/", "~1");

    internal static bool IsSafeRelativePath(string? path, string? pluginDirectory, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path must not be empty.";
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            error = "Path must be relative to the plugin directory.";
            return false;
        }

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            error = "Path must not escape the plugin directory.";
            return false;
        }

        if (pluginDirectory is not null)
        {
            var directory = Path.GetFullPath(pluginDirectory);
            var combined = Path.GetFullPath(Path.Combine(directory, path));
            var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(prefix, StringComparison.Ordinal)
                && !string.Equals(combined, Path.TrimEndingDirectorySeparator(directory), StringComparison.Ordinal))
            {
                error = "Path escapes the plugin directory.";
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
