using Natter.Application.Admin;

namespace Natter.Plugin.WebConsole;

/// <summary>
/// Explicit host admin contracts made available to this plugin by the NPS host
/// (<c>NpsPluginRuntime.BindAdminServices</c>). Every contract is bound only when the manifest
/// holds the matching <c>admin.*</c> permission, so a missing service means "not granted or host
/// admin surface disabled" — the console then reports the documented gap instead of failing.
/// </summary>
public sealed record WebConsoleAdminSurface(
    IAdminConfigStore? ConfigStore,
    IAdminCommandRunner? CommandRunner,
    IAdminLogReader? LogReader,
    IAdminRedactor? Redactor)
{
    public static WebConsoleAdminSurface Empty { get; } = new(null, null, null, null);

    public bool HasConfigStore => ConfigStore is not null;

    public bool HasCommandRunner => CommandRunner is not null;

    public bool HasLogReader => LogReader is not null;

    /// <summary>Resolves the admin contracts from the plugin provider; never throws.</summary>
    public static WebConsoleAdminSurface FromServices(IServiceProvider? provider)
    {
        if (provider is null)
            return Empty;

        return new WebConsoleAdminSurface(
            Resolve<IAdminConfigStore>(provider),
            Resolve<IAdminCommandRunner>(provider),
            Resolve<IAdminLogReader>(provider),
            Resolve<IAdminRedactor>(provider));
    }

    public string DescribeConfig()
        => HasConfigStore ? "available" : WebConsoleGaps.AdminConfig.Code;

    public string DescribeCommand()
        => HasCommandRunner ? "available" : WebConsoleGaps.AdminCommand.Code;

    private static TService? Resolve<TService>(IServiceProvider provider)
        where TService : class
    {
        try
        {
            return provider.GetService(typeof(TService)) as TService;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>One rejected admin operation, already mapped to an HTTP status and a stable code.</summary>
public sealed record WebConsoleAdminFailure(
    string Code,
    string Message,
    int StatusCode,
    string? Doc = null,
    object? Detail = null)
{
    public string Documentation => Doc ?? WebConsoleDefaults.DefaultDocumentationFile;
}

/// <summary>Result of a gateway call: either a value or a mapped failure. No exceptions leak out.</summary>
public sealed record WebConsoleAdminResult<T>(T? Value, WebConsoleAdminFailure? Failure)
{
    public bool Ok => Failure is null && Value is not null;

    public static WebConsoleAdminResult<T> Success(T value) => new(value, null);

    public static WebConsoleAdminResult<T> Fail(string code, string message, int statusCode, string? doc = null)
        => new(default, new WebConsoleAdminFailure(code, message, statusCode, doc));

    public static WebConsoleAdminResult<T> Fail(WebConsoleAdminFailure failure) => new(default, failure);
}

/// <summary>
/// Maps host <see cref="AdminCodes"/> to the WebConsole HTTP contract. Codes are deliberately
/// identical where the plugin already used them, so API clients see one vocabulary.
/// </summary>
public static class WebConsoleAdminFailures
{
    private const string ConfigDoc = "docs/WEBCONSOLE_HOST_API_GAP.md#4-g5配置编辑";
    private const string CommandDoc = "docs/WEBCONSOLE_HOST_API_GAP.md#5-g6受限命令终端";

    public static WebConsoleAdminFailure ConfigUnavailable { get; } = new(
        WebConsoleGaps.AdminConfig.Code,
        WebConsoleGaps.AdminConfig.Summary,
        501,
        ConfigDoc);

    public static WebConsoleAdminFailure CommandUnavailable { get; } = new(
        WebConsoleGaps.AdminCommand.Code,
        WebConsoleGaps.AdminCommand.Summary,
        501,
        CommandDoc);

    public static WebConsoleAdminFailure ConfigHostFailed(string message) => new(
        "host.admin_config.failed",
        message,
        502,
        ConfigDoc);

    public static WebConsoleAdminFailure CommandHostFailed(string message) => new(
        "host.admin_command.failed",
        message,
        502,
        CommandDoc);

    public static WebConsoleAdminFailure FromConfigCode(string? code, string? message)
    {
        var text = message ?? "The host rejected the config request.";
        return code switch
        {
            AdminCodes.ConfigEtagMismatch => new("config.conflict", text, 409, ConfigDoc),
            AdminCodes.ConfigCreateRequiresMissing => new("config.conflict", text, 409, ConfigDoc),
            AdminCodes.ConfigLocked => new("config.locked", text, 409, ConfigDoc),
            AdminCodes.ConfigSymlinkNotAllowed => new("config.symlink", text, 409, ConfigDoc),
            AdminCodes.ConfigPluginUnknown => new("config.plugin_unknown", text, 404, ConfigDoc),
            AdminCodes.ConfigNotFound => new("config.not_found", text, 404, ConfigDoc),
            AdminCodes.ConfigIdInvalid => new("config.id_invalid", text, 400, ConfigDoc),
            AdminCodes.ConfigDocumentNotAllowed => new("config.id_invalid", text, 400, ConfigDoc),
            AdminCodes.ConfigInvalid
                or AdminCodes.ConfigInvalidJson
                or AdminCodes.ConfigSchemaInvalid
                or AdminCodes.ConfigTooLarge
                or AdminCodes.ConfigControlChars
                or AdminCodes.ConfigSecretLiteral
                or AdminCodes.ConfigRootType => new("config.invalid", text, 400, ConfigDoc),
            AdminCodes.ConfigDirectoryUnavailable
                or AdminCodes.ConfigUnavailable => ConfigUnavailable,
            AdminCodes.ConfigBackupFailed
                or AdminCodes.ConfigWriteFailed => ConfigHostFailed(text),
            null or "" => ConfigHostFailed(text),
            _ => ConfigHostFailed(text),
        };
    }

    public static WebConsoleAdminFailure FromCommandCode(string? code, string? message)
    {
        var text = message ?? "The host rejected the command.";
        return code switch
        {
            AdminCodes.CommandNotAllowed => new("command.not_allowed", text, 403, CommandDoc),
            AdminCodes.CommandNotRegistered => new("command.not_registered", text, 400, CommandDoc),
            AdminCodes.CommandInvalid => new("command.invalid", text, 400, CommandDoc),
            AdminCodes.CommandTimeout => new("command.timeout", text, 504, CommandDoc),
            AdminCodes.CommandBusy => new("command.busy", text, 409, CommandDoc),
            AdminCodes.CommandCancelled => new("command.cancelled", text, 408, CommandDoc),
            null or "" => CommandHostFailed(text),
            _ => CommandHostFailed(text),
        };
    }
}
