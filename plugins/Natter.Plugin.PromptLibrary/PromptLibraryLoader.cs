using System.Text;
using System.Text.RegularExpressions;

namespace Natter.Plugin.PromptLibrary;

/// <summary>Stable loader/startup codes (used by tests and diagnostics; never contain prompt text).</summary>
public static class PromptLibraryCodes
{
    public const string ConfigInvalid = "promptlibrary.config-invalid";
    public const string NotConfigured = "promptlibrary.not-configured";
    public const string FragmentInvalid = "promptlibrary.fragment-invalid";
    public const string FragmentDuplicate = "promptlibrary.fragment-duplicate";
    public const string FragmentMissing = "promptlibrary.fragment-missing";
    public const string FragmentPathInvalid = "promptlibrary.fragment-path-invalid";
    public const string FragmentTooLarge = "promptlibrary.fragment-too-large";
    public const string FragmentNotUtf8 = "promptlibrary.fragment-not-utf8";
    public const string FragmentMandatory = "promptlibrary.fragment-mandatory-not-allowed";
    public const string PresetInvalid = "promptlibrary.preset-invalid";
    public const string PresetDuplicate = "promptlibrary.preset-duplicate";
    public const string PresetMissing = "promptlibrary.preset-missing";
    public const string PresetMissingBase = "promptlibrary.preset-missing-base";
    public const string PresetCycle = "promptlibrary.preset-cycle";
    public const string PresetFragmentMissing = "promptlibrary.preset-fragment-missing";
    public const string PresetFileInvalid = "promptlibrary.preset-file-invalid";
}

/// <summary>Raised inside the loader; converted to a <see cref="PromptLibraryLoadResult"/>.</summary>
internal sealed class PromptLibraryLoadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Loaded, immutable prompt library data handed to the provider.</summary>
public sealed record PromptLibrary(
    bool Enabled,
    IReadOnlyList<Natter.Application.Chat.PromptFragment> Fragments,
    IReadOnlyList<Natter.Application.Chat.PromptPreset> Presets,
    string? ActivePreset,
    string? Locale,
    Natter.Application.Chat.PromptBudget Budget,
    IReadOnlyList<string> DisabledFragments,
    Natter.Application.Chat.PromptMissingVariableBehavior MissingVariables,
    IReadOnlyList<string> Warnings);

public sealed record PromptLibraryLoadResult(
    PromptLibrary? Library,
    string? Code,
    string? Message)
{
    public bool Success => Library is not null;

    public static PromptLibraryLoadResult Ok(PromptLibrary library) => new(library, null, null);

    public static PromptLibraryLoadResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Loads the Prompt Library plugin data. File fragments are resolved inside
/// <see cref="PromptLibraryOptions.PromptDirectory"/> only: absolute paths, <c>..</c>, drive
/// letters, symlinks and reparse points are rejected, files are read-only, size-limited and must
/// be valid UTF-8.
/// </summary>
public static class PromptLibraryLoader
{
    public const int MaxFragmentFileBytes = 64 * 1024;

    public const int MaxPresetFileBytes = 256 * 1024;

    private static readonly Regex IdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:#-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static PromptLibraryLoadResult Load(PromptLibraryOptions options, string pluginDirectory)
    {
        try
        {
            return PromptLibraryLoadResult.Ok(Build(options, pluginDirectory));
        }
        catch (PromptLibraryLoadException exception)
        {
            return PromptLibraryLoadResult.Fail(exception.Code, exception.Message);
        }
    }

    private static PromptLibrary Build(PromptLibraryOptions options, string pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            throw new PromptLibraryLoadException(PromptLibraryCodes.ConfigInvalid, "Plugin directory is unknown.");

        if (options.MaxCharacters <= 0 || options.MaxFragmentCharacters <= 0)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.ConfigInvalid,
                "maxCharacters and maxFragmentCharacters must be positive.");

        var missingVariables = ParseMissingVariables(options.MissingVariables);
        var baseDirectory = ResolvePromptDirectory(pluginDirectory, options.PromptDirectory);

        var fragmentDefinitions = new List<PromptFragmentDefinition>(options.Fragments ?? []);
        var presetDefinitions = new List<PromptPresetDefinition>(options.Presets ?? []);

        if (!string.IsNullOrWhiteSpace(options.PresetFile))
        {
            var file = ReadPresetFile(pluginDirectory, options.PresetFile);
            fragmentDefinitions.AddRange(file.Fragments ?? []);
            presetDefinitions.AddRange(file.Presets ?? []);
        }

        var fragments = new List<Natter.Application.Chat.PromptFragment>();
        var fragmentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in fragmentDefinitions)
        {
            var fragment = MapFragment(definition, baseDirectory, options.MaxFragmentCharacters);
            if (!fragmentIds.Add(fragment.Id))
                throw new PromptLibraryLoadException(
                    PromptLibraryCodes.FragmentDuplicate,
                    $"Fragment id '{fragment.Id}' is declared more than once.");

            fragments.Add(fragment);
        }

        var presets = new List<Natter.Application.Chat.PromptPreset>();
        var presetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in presetDefinitions)
        {
            var preset = MapPreset(definition);
            if (!presetIds.Add(preset.Id))
                throw new PromptLibraryLoadException(
                    PromptLibraryCodes.PresetDuplicate,
                    $"Preset id '{preset.Id}' is declared more than once.");

            presets.Add(preset);
        }

        if (fragments.Count == 0)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.NotConfigured,
                "No prompt fragments are configured; copy config.sample.json to config.json and adjust it.");

        ValidateReferences(fragments, presets, options.DisabledFragments ?? [], out var warnings);

        if (!string.IsNullOrWhiteSpace(options.Preset) && presets.All(preset => preset.Id != options.Preset))
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.PresetMissing,
                $"Configured preset '{options.Preset}' is not declared.");

        return new PromptLibrary(
            options.Enabled,
            fragments,
            presets,
            string.IsNullOrWhiteSpace(options.Preset) ? null : options.Preset,
            string.IsNullOrWhiteSpace(options.Locale) ? null : options.Locale,
            new Natter.Application.Chat.PromptBudget(options.MaxCharacters, options.MaxFragmentCharacters),
            (options.DisabledFragments ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            missingVariables,
            warnings);
    }

    private static string ResolvePromptDirectory(string pluginDirectory, string? promptDirectory)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(promptDirectory)
            ? Path.GetFullPath(pluginDirectory)
            : PromptLibraryPaths.ResolveUnder(pluginDirectory, promptDirectory, PromptLibraryCodes.FragmentPathInvalid);

        return baseDirectory;
    }

    private static PromptLibraryFile ReadPresetFile(string pluginDirectory, string presetFile)
    {
        var path = PromptLibraryPaths.ResolveUnder(pluginDirectory, presetFile, PromptLibraryCodes.FragmentPathInvalid);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentMissing,
                $"Preset file '{presetFile}' does not exist.");

        if (info.Length > MaxPresetFileBytes)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentTooLarge,
                $"Preset file '{presetFile}' exceeds {MaxPresetFileBytes} bytes.");

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PromptLibraryFile>(
                       File.ReadAllText(path, StrictUtf8),
                       new System.Text.Json.JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true,
                           ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                           AllowTrailingCommas = true,
                       })
                   ?? new PromptLibraryFile();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.PresetFileInvalid,
                $"Preset file '{presetFile}' could not be read as UTF-8 JSON.");
        }
    }

    private static Natter.Application.Chat.PromptFragment MapFragment(
        PromptFragmentDefinition definition,
        string baseDirectory,
        int maxFragmentCharacters)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PromptLibraryLoadException(PromptLibraryCodes.FragmentInvalid, "A fragment is missing its id.");

        if (!IdPattern.IsMatch(definition.Id))
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentInvalid,
                $"Fragment id '{definition.Id}' must match [A-Za-z0-9][A-Za-z0-9._:#-]{{0,127}}.");

        if (definition.Mandatory)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentMandatory,
                $"Fragment '{definition.Id}' declares mandatory=true; only the host may create mandatory fragments.");

        var hasText = !string.IsNullOrWhiteSpace(definition.Text);
        var hasFile = !string.IsNullOrWhiteSpace(definition.File);
        if (hasText == hasFile)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentInvalid,
                $"Fragment '{definition.Id}' must define exactly one of 'text' or 'file'.");

        var source = hasFile
            ? Natter.Application.Chat.PromptFragmentSource.File
            : Natter.Application.Chat.PromptFragmentSource.Inline;
        var text = hasFile
            ? PromptLibraryPaths.ReadTextFile(baseDirectory, definition.File!, MaxFragmentFileBytes)
            : definition.Text!.TrimEnd('\r', '\n');

        if (string.IsNullOrWhiteSpace(text))
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentInvalid,
                $"Fragment '{definition.Id}' resolves to empty text.");

        var limit = definition.MaxCharacters <= 0
            ? maxFragmentCharacters
            : Math.Min(definition.MaxCharacters, maxFragmentCharacters);

        return new Natter.Application.Chat.PromptFragment(
            definition.Id,
            text,
            source,
            definition.Order,
            definition.EnabledByDefault,
            definition.CanDisable,
            Mandatory: false,
            MaxCharacters: limit,
            Version: definition.Version,
            Tags: definition.Tags is { Count: > 0 } tags ? tags.ToArray() : null);
    }

    private static Natter.Application.Chat.PromptPreset MapPreset(PromptPresetDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new PromptLibraryLoadException(PromptLibraryCodes.PresetInvalid, "A preset is missing its id.");

        if (!IdPattern.IsMatch(definition.Id))
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.PresetInvalid,
                $"Preset id '{definition.Id}' must match [A-Za-z0-9][A-Za-z0-9._:#-]{{0,127}}.");

        if (string.IsNullOrWhiteSpace(definition.Version))
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.PresetInvalid,
                $"Preset '{definition.Id}' must declare a version.");

        var sections = new List<Natter.Application.Chat.PromptSectionRef>();
        foreach (var section in definition.Sections ?? [])
        {
            if (string.IsNullOrWhiteSpace(section.FragmentId))
                throw new PromptLibraryLoadException(
                    PromptLibraryCodes.PresetInvalid,
                    $"Preset '{definition.Id}' has a section without fragmentId.");

            sections.Add(new Natter.Application.Chat.PromptSectionRef(
                section.FragmentId,
                section.Order,
                section.Enabled,
                section.Source,
                section.OverrideText,
                section.Reason));
        }

        return new Natter.Application.Chat.PromptPreset(
            definition.Id,
            definition.Version,
            sections,
            string.IsNullOrWhiteSpace(definition.Extends) ? null : definition.Extends,
            string.IsNullOrWhiteSpace(definition.Locale) ? null : definition.Locale,
            definition.Variables is { Count: > 0 } variables
                ? new Dictionary<string, string>(variables, StringComparer.Ordinal)
                : null);
    }

    private static void ValidateReferences(
        IReadOnlyList<Natter.Application.Chat.PromptFragment> fragments,
        IReadOnlyList<Natter.Application.Chat.PromptPreset> presets,
        IReadOnlyList<string> disabledFragments,
        out IReadOnlyList<string> warnings)
    {
        var fragmentIds = fragments.Select(fragment => fragment.Id).ToHashSet(StringComparer.Ordinal);
        var byId = presets.ToDictionary(preset => preset.Id, StringComparer.Ordinal);
        var warningList = new List<string>();

        foreach (var preset in presets)
        {
            foreach (var section in preset.Sections ?? [])
            {
                if (!fragmentIds.Contains(section.FragmentId))
                    throw new PromptLibraryLoadException(
                        PromptLibraryCodes.PresetFragmentMissing,
                        $"Preset '{preset.Id}' references missing fragment '{section.FragmentId}'.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = preset;
            while (true)
            {
                if (!seen.Add(current.Id))
                    throw new PromptLibraryLoadException(
                        PromptLibraryCodes.PresetCycle,
                        $"Preset inheritance contains a cycle at '{current.Id}'.");

                if (string.IsNullOrWhiteSpace(current.Extends))
                    break;

                if (!byId.TryGetValue(current.Extends, out var basePreset))
                    throw new PromptLibraryLoadException(
                        PromptLibraryCodes.PresetMissingBase,
                        $"Preset '{current.Id}' extends missing preset '{current.Extends}'.");

                current = basePreset;
            }
        }

        foreach (var id in disabledFragments)
        {
            if (!fragmentIds.Contains(id))
                warningList.Add($"promptlibrary.disabled-unknown:{id}");
        }

        warnings = warningList;
    }

    private static Natter.Application.Chat.PromptMissingVariableBehavior ParseMissingVariables(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "warn" or "warning" => Natter.Application.Chat.PromptMissingVariableBehavior.Warning,
            "error" or "strict" => Natter.Application.Chat.PromptMissingVariableBehavior.Error,
            _ => throw new PromptLibraryLoadException(
                PromptLibraryCodes.ConfigInvalid,
                "missingVariables must be 'warn' or 'error'."),
        };
}

/// <summary>Path policy and safe file reading for prompt fragments.</summary>
public static class PromptLibraryPaths
{
    public static string ResolvePluginDirectory(string? assemblyLocation)
    {
        var directory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        if (string.Equals(Path.GetFileName(directory), "lib", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(directory);
            if (!string.IsNullOrEmpty(parent))
                return Path.GetFullPath(parent);
        }

        return Path.GetFullPath(directory);
    }

    /// <summary>Resolves a relative path below <paramref name="baseDirectory"/> or throws a coded exception.</summary>
    public static string ResolveUnder(string baseDirectory, string relativePath, string code)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new PromptLibraryLoadException(code, "Path must not be empty.");

        var trimmed = relativePath.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.Contains(':', StringComparison.Ordinal))
            throw new PromptLibraryLoadException(code, $"Path '{trimmed}' must be relative.");

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new PromptLibraryLoadException(code, $"Path '{trimmed}' must not contain '.' or '..'.");
        }

        var baseFull = Path.GetFullPath(baseDirectory);
        var full = Path.GetFullPath(Path.Combine(baseFull, trimmed));
        if (!IsUnder(baseFull, full))
            throw new PromptLibraryLoadException(code, $"Path '{trimmed}' escapes the plugin directory.");

        EnsureNoLink(baseFull, full, code);
        return full;
    }

    /// <summary>Reads a UTF-8 fragment file with a byte limit; never writes.</summary>
    public static string ReadTextFile(string baseDirectory, string relativePath, int maxBytes)
    {
        var path = ResolveUnder(baseDirectory, relativePath, PromptLibraryCodes.FragmentPathInvalid);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentMissing,
                $"Fragment file '{relativePath}' does not exist.");

        if (info.Length > maxBytes)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentTooLarge,
                $"Fragment file '{relativePath}' exceeds {maxBytes} bytes.");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentMissing,
                $"Fragment file '{relativePath}' could not be read.");
        }

        if (bytes.Length > maxBytes)
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentTooLarge,
                $"Fragment file '{relativePath}' exceeds {maxBytes} bytes.");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new PromptLibraryLoadException(
                PromptLibraryCodes.FragmentNotUtf8,
                $"Fragment file '{relativePath}' is not valid UTF-8.");
        }

        if (text.StartsWith('\uFEFF'))
            text = text[1..];

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
    }

    private static bool IsUnder(string baseDirectory, string path)
    {
        var normalizedBase = Path.TrimEndingDirectorySeparator(baseDirectory);
        return string.Equals(path, normalizedBase, StringComparison.Ordinal)
               || path.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void EnsureNoLink(string baseDirectory, string path, string code)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current) && IsUnder(baseDirectory, current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists)
            {
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new PromptLibraryLoadException(code, "Symbolic links are not allowed for prompt fragments.");

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null && !IsUnder(baseDirectory, Path.GetFullPath(target.FullName)))
                    throw new PromptLibraryLoadException(code, "Prompt fragment resolves outside the plugin directory.");
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(current), Path.TrimEndingDirectorySeparator(baseDirectory), StringComparison.Ordinal))
                break;

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }
    }
}
