namespace Natter.Plugin.Abstractions;

/// <summary>NPS v1 permission vocabulary. Permissions are host governance, not a sandbox.</summary>
public static class PluginPermissions
{
    public const string Network = "network";
    public const string FileSystemRead = "filesystem.read";
    public const string FileSystemWrite = "filesystem.write";
    public const string SecretsPrefix = "secrets:";
    public const string ChatRead = "chat.read";
    public const string ChatSend = "chat.send";
    public const string ChatEdit = "chat.edit";
    public const string ChatDelete = "chat.delete";
    public const string ChatCommands = "chat.commands";
    public const string ChatInteractions = "chat.interactions";
    public const string AiComplete = "ai.complete";
    public const string Storage = "storage";
    public const string CliCommands = "cli.commands";

    public static IReadOnlyList<string> All { get; } =
    [
        Network,
        FileSystemRead,
        FileSystemWrite,
        ChatRead,
        ChatSend,
        ChatEdit,
        ChatDelete,
        ChatCommands,
        ChatInteractions,
        AiComplete,
        Storage,
        CliCommands,
    ];

    public static string Secret(string name) => SecretsPrefix + name;

    public static bool IsKnown(string? permission)
        => permission is not null
           && (All.Contains(permission, StringComparer.Ordinal)
               || permission.StartsWith(SecretsPrefix, StringComparison.Ordinal)
                  && permission.Length > SecretsPrefix.Length);
}

/// <summary>Trust level of a plugin package.</summary>
public enum PluginTrustLevel
{
    Bundled,
    Signed,
    Local,
    Untrusted,
}
