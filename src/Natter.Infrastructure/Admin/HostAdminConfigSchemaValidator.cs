using System.Text.Json;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Plugins;
using Natter.Application.Admin;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Admin;

/// <summary>Schema-level validation for a whitelisted config document.</summary>
public interface IAdminConfigSchemaValidator
{
    ValueTask<IReadOnlyList<AdminConfigIssue>> ValidateAsync(
        string normalizedId,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates <c>profiles</c> against the host profile rules and <c>plugin:&lt;id&gt;</c> against the
/// plugin's declared <c>config.schema.json</c> using the same schema subset the NPS loader uses.
/// </summary>
public sealed class HostAdminConfigSchemaValidator : IAdminConfigSchemaValidator
{
    private readonly IPluginCatalog _catalog;
    private readonly ILogger<HostAdminConfigSchemaValidator> _logger;

    public HostAdminConfigSchemaValidator(
        IPluginCatalog catalog,
        ILogger<HostAdminConfigSchemaValidator> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValueTask<IReadOnlyList<AdminConfigIssue>> ValidateAsync(
        string normalizedId,
        string content,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(normalizedId, AdminConfigDocumentPolicy.ProfilesId, StringComparison.Ordinal))
            return ValueTask.FromResult(ProfilesConfigValidator.Validate(content));

        if (!AdminConfigDocumentPolicy.TryGetPluginId(normalizedId, out var pluginId))
        {
            return ValueTask.FromResult<IReadOnlyList<AdminConfigIssue>>(
            [
                new AdminConfigIssue("/id", AdminCodes.ConfigIdInvalid, "Config id must be 'profiles' or 'plugin:<plugin-id>'."),
            ]);
        }

        var descriptor = _catalog.Plugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Id, pluginId, StringComparison.Ordinal));
        if (descriptor is null)
        {
            return ValueTask.FromResult<IReadOnlyList<AdminConfigIssue>>(
            [
                new AdminConfigIssue("/id", AdminCodes.ConfigPluginUnknown, $"Plugin '{pluginId}' is not registered in this host."),
            ]);
        }

        JsonElement schema;
        try
        {
            var resolved = TryReadSchema(descriptor);
            if (resolved.Issues.Count > 0 || resolved.Schema is null)
                return ValueTask.FromResult(resolved.Issues);

            schema = resolved.Schema.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Admin config schema for plugin {PluginId} could not be read.", pluginId);
            return ValueTask.FromResult<IReadOnlyList<AdminConfigIssue>>(
            [
                new AdminConfigIssue("$", AdminCodes.ConfigSchemaInvalid, $"Config schema for plugin '{pluginId}' could not be read."),
            ]);
        }

        JsonElement config;
        try
        {
            using var document = JsonDocument.Parse(content, ProfilesConfigValidator.ParseOptions);
            config = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Content validation already reported invalid JSON; schema validation is skipped.
            return ValueTask.FromResult<IReadOnlyList<AdminConfigIssue>>([]);
        }

        var effective = PluginConfigSchema.ApplyDefaults(schema, config);
        var issues = PluginConfigSchema.Validate(schema, effective)
            .Select(issue => new AdminConfigIssue(
                string.IsNullOrEmpty(issue.Pointer) ? "$" : issue.Pointer,
                AdminCodes.ConfigSchemaInvalid,
                issue.Message,
                issue.Expected,
                issue.Actual))
            .ToArray();

        return ValueTask.FromResult<IReadOnlyList<AdminConfigIssue>>(issues);
    }

    private static (JsonElement? Schema, IReadOnlyList<AdminConfigIssue> Issues) TryReadSchema(PluginDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Directory))
            return (null, []);

        var pluginDirectory = Path.GetFullPath(descriptor.Directory);
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        if (!File.Exists(manifestPath))
            return (null, []);

        var read = PluginManifestReader.Read(pluginDirectory);
        var manifest = read.Manifest;
        if (manifest?.ConfigSchema is null)
            return (null, []);

        var schemaPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifest.ConfigSchema));
        var prefix = Path.TrimEndingDirectorySeparator(pluginDirectory) + Path.DirectorySeparatorChar;
        if (!schemaPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null,
            [
                new AdminConfigIssue(
                    "$",
                    AdminCodes.ConfigSchemaInvalid,
                    "Declared config schema resolves outside the plugin directory.",
                    "path inside plugin directory",
                    manifest.ConfigSchema),
            ]);
        }

        if (!File.Exists(schemaPath) || new FileInfo(schemaPath).LinkTarget is not null)
        {
            return (null,
            [
                new AdminConfigIssue(
                    "$",
                    AdminCodes.ConfigSchemaInvalid,
                    "Declared config schema was not found or is a symbolic link.",
                    manifest.ConfigSchema,
                    null),
            ]);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath), ProfilesConfigValidator.ParseOptions);
        return (document.RootElement.Clone(), []);
    }
}
