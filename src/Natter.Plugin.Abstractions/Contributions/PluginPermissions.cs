namespace Natter.Plugin.Abstractions;

/// <summary>NPS v1 permission vocabulary. Permissions are host governance, not a sandbox.</summary>
public static class PluginPermissions
{
    public const string Network = "network";
    public const string FileSystemRead = "filesystem.read";
    public const string FileSystemWrite = "filesystem.write";
    public const string SecretsPrefix = "secrets:";
    /// <summary>
    /// Read NLMP messages/events and the conversation/memory/summary context handed to prompt
    /// contributions. Required by <c>context.assembler</c>, <c>prompt.provider</c> and
    /// <c>prompt.fragment</c> (S2-C2).
    /// </summary>
    public const string ChatRead = "chat.read";
    public const string ChatSend = "chat.send";
    public const string ChatEdit = "chat.edit";
    public const string ChatDelete = "chat.delete";
    public const string ChatCommands = "chat.commands";
    public const string ChatInteractions = "chat.interactions";
    public const string AiComplete = "ai.complete";
    public const string Storage = "storage";
    public const string CliCommands = "cli.commands";

    /// <summary>Allows a plugin to read the admin diagnostics snapshot and the host log tail (G4/G8).</summary>
    public const string AdminRead = "admin.read";

    /// <summary>Allows a plugin to read/write the whitelisted admin config documents (G5).</summary>
    public const string AdminConfigWrite = "admin.config.write";

    /// <summary>Allows a plugin to run allowlisted Natter CLI commands in-process (G6).</summary>
    public const string AdminCommandExecute = "admin.command.execute";

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
        AdminRead,
        AdminConfigWrite,
        AdminCommandExecute,
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
