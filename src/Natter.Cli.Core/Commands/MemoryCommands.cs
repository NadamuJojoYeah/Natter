using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>
/// Explicit, user-confirmed memory (P6.7 layered contract). No model inference is performed here.
/// </summary>
public sealed class RememberCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var text = string.Join(' ', invocation.Positionals);
        if (string.IsNullOrWhiteSpace(text))
        {
            invocation.Console.Error.WriteLine(
                "usage: remember <text> [--type policy|preference|fact|episodic|trace] [--scope user|conversation|guild]");
            return CliExitCodes.Usage;
        }

        if (!TryParseType(invocation.GetOption("type", invocation.GetOption("kind", "preference")), out var type))
        {
            invocation.Console.Error.WriteLine("remember: --type must be policy|preference|fact|episodic|trace");
            return CliExitCodes.Usage;
        }

        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var item = await selectedStore.WriteAsync(
            new MemoryEntryWriteRequest(
                context.Source,
                context.Conversation,
                context.User,
                type,
                text,
                context.Scope,
                Confidence: 1.0,
                Sensitivity: invocation.GetOption("sensitivity", "normal")),
            cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(item, JsonOutput.CompactOptions));
        else
            invocation.Console.Out.WriteLine(
                $"remembered {item.Id} type={MemoryEntryMapping.ToStorageValue(item.Type)} scope={item.ScopeType.ToString().ToLowerInvariant()}");

        return CliExitCodes.Success;
    }

    internal static bool TryParseType(string? value, out MemoryType type)
        => MemoryEntryMapping.TryParseStorageValue(value, out type);
}

/// <summary>
/// Archives a memory. v2 never hard-deletes (§5.3): the entry stops being recalled but stays
/// recoverable, so the command reports that explicitly instead of claiming a delete.
/// </summary>
public sealed class ForgetCommand(IProfileMemoryStoreResolver resolver, ISummaryStore summaries) : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var value = string.Join(' ', invocation.Positionals);
        if (string.IsNullOrWhiteSpace(value))
        {
            invocation.Console.Error.WriteLine("usage: forget <memory-id|text>");
            return CliExitCodes.Usage;
        }

        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);

        var entryId = value.StartsWith("mem_", StringComparison.Ordinal) || value.StartsWith("me_", StringComparison.Ordinal)
            ? value
            : await FindByContentAsync(selectedStore, context, value, cancellationToken).ConfigureAwait(false);

        var archive = entryId is null
            ? MemoryArchiveResult.NotFound
            : await selectedStore.ArchiveAsync(
                context.Source, context.Conversation, context.User, entryId, cancellationToken)
                .ConfigureAwait(false);

        var conversationKey = new Natter.Application.Chat.ConversationKey(
            context.Source.Key, context.Conversation.Id, context.Conversation.GuildId, null);
        await summaries.InvalidateAsync(conversationKey, "forget", cancellationToken).ConfigureAwait(false);
        await summaries.InvalidateAsync(
            conversationKey with { ActorId = context.User.Id }, "forget", cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(
                new { status = archive.Status.ToString().ToLowerInvariant(), id = entryId },
                JsonOutput.CompactOptions));
            return archive.Succeeded ? CliExitCodes.Success : CliExitCodes.Failure;
        }

        switch (archive.Status)
        {
            case MemoryArchiveStatus.Archived:
                invocation.Console.Out.WriteLine(
                    $"archived {entryId}: removed from recall, still recoverable (P6 §5.3)");
                return CliExitCodes.Success;
            case MemoryArchiveStatus.NotOwner:
                invocation.Console.Error.WriteLine(
                    "cannot archive: this memory belongs to another user (only its creator may archive it)");
                return CliExitCodes.Failure;
            default:
                invocation.Console.Error.WriteLine("cannot archive: no such memory is visible to this actor");
                return CliExitCodes.Failure;
        }
    }

    private static async ValueTask<string?> FindByContentAsync(
        IMemoryEntryStore store,
        MemoryCommandContext context,
        string text,
        CancellationToken cancellationToken)
    {
        var matches = await store.SearchAsync(
            new MemoryEntryQuery(context.Source, context.Conversation, context.User)
            {
                Query = text,
                IncludeArchived = true,
                Limit = 20,
            },
            cancellationToken).ConfigureAwait(false);

        return matches
            .FirstOrDefault(entry => string.Equals(entry.Content, text, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}

/// <summary>
/// Lists visible entries. Memory text is user content: it is printed only with <c>--verbose</c> and
/// never written to audit or diagnostics (§10.2). The default view is id + type + scope + length + time.
/// </summary>
public sealed class MemoryListCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var items = await selectedStore.SearchAsync(
            new MemoryEntryQuery(context.Source, context.Conversation, context.User)
            {
                Limit = invocation.GetIntOption("limit", 50),
                IncludeArchived = invocation.HasFlag("all"),
                IncludePrivate = context.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group,
            },
            cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(items, JsonOutput.CompactOptions));
            return CliExitCodes.Success;
        }

        var verbose = invocation.HasFlag("verbose");
        foreach (var item in items)
        {
            var archived = item.Archived ? " (archived)" : string.Empty;
            var body = verbose ? " " + item.Content : string.Empty;
            invocation.Console.Out.WriteLine(
                $"{item.Id} [{MemoryEntryMapping.ToStorageValue(item.Type)}/{item.ScopeType.ToString().ToLowerInvariant()}] "
                + $"len={item.Content.Length} S={item.StabilityDays:F1}d updated={item.UpdatedAt:yyyy-MM-dd}{archived}{body}");
        }

        invocation.Console.Out.WriteLine($"count: {items.Count}");
        return CliExitCodes.Success;
    }
}

internal sealed record MemoryCommandContext(Source Source, Conversation Conversation, User User, MemoryScopeType Scope)
{
    public static MemoryCommandContext Parse(CliInvocation invocation)
    {
        var sourceParts = invocation.GetOption("source", "discord:discord:cli").Split(':');
        var source = Source.Create(
            sourceParts.Length > 0 ? sourceParts[0] : "discord",
            sourceParts.Length > 1 ? sourceParts[1] : "discord",
            sourceParts.Length > 2 ? sourceParts[2] : "cli");
        var guild = invocation.GetOption("guild");
        var conversation = new Conversation(
            invocation.GetOption("conversation", "cli"),
            guild is null ? ConversationKind.Direct : ConversationKind.Guild,
            GuildId: guild);
        var user = new User(invocation.GetOption("actor", "cli-user"), "CLI User");
        var scope = invocation.GetOption("scope", "user") switch
        {
            "conversation" => MemoryScopeType.Conversation,
            "guild" => MemoryScopeType.Guild,
            // Global is never a silent default (§10.1): an operator must ask for it by name.
            "global" => MemoryScopeType.Global,
            _ => MemoryScopeType.User,
        };
        return new MemoryCommandContext(source, conversation, user, scope);
    }
}

/// <summary>Exports visible memory as the versioned v2 envelope (memory only, no summary text).</summary>
public sealed class MemoryExportCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var export = await selectedStore.ExportAsync(
            context.Source,
            context.Conversation,
            context.User,
            includePrivate: context.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group,
            cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(export, JsonOutput.CompactOptions);
        var output = invocation.GetOption("output");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var path = Path.GetFullPath(output);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            invocation.Console.Out.WriteLine(
                $"exported {export.Items.Count} memory item(s) schemaVersion={export.SchemaVersion} to {path}");
        }
        else
        {
            invocation.Console.Out.WriteLine(json);
        }

        return CliExitCodes.Success;
    }
}

/// <summary>
/// Imports a v2 envelope, or a v1 envelope upgraded through <see cref="MemoryExportBridge"/>.
/// A newer schemaVersion is refused rather than silently truncated (§11).
/// </summary>
public sealed class MemoryImportCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var file = invocation.GetOption("file");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            invocation.Console.Error.WriteLine("usage: memory import --file <json> --source ... --actor ...");
            return CliExitCodes.Usage;
        }

        MemoryEntryExport? export;
        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            export = ReadEnvelope(text);
        }
        catch (JsonException exception)
        {
            invocation.Console.Error.WriteLine($"invalid memory export: {exception.Message}");
            return CliExitCodes.Usage;
        }
        catch (MemoryExportVersionException exception)
        {
            invocation.Console.Error.WriteLine(exception.Message);
            return CliExitCodes.Failure;
        }

        if (export is null)
        {
            invocation.Console.Error.WriteLine("invalid memory export: file contains neither a v1 nor a v2 envelope");
            return CliExitCodes.Usage;
        }

        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var imported = await selectedStore.ImportAsync(
            context.Source, context.Conversation, context.User, export, cancellationToken).ConfigureAwait(false);

        // Silently importing nothing would hide a scope mismatch: an envelope exported for one actor
        // must not look like a successful import into another.
        if (imported == 0 && export.Items.Count > 0)
        {
            invocation.Console.Error.WriteLine(
                $"memory import: {export.Items.Count} item(s) do not belong to this source/actor/scope; nothing imported");
            return CliExitCodes.Failure;
        }

        if (invocation.HasFlag("json"))
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(new { imported }, JsonOutput.CompactOptions));
        else
            invocation.Console.Out.WriteLine($"imported {imported} memory item(s) schemaVersion={export.SchemaVersion}");

        return CliExitCodes.Success;
    }

    /// <summary>Reads a v2 envelope, or upgrades a v1 one. Returns null when neither shape matches.</summary>
    private static MemoryEntryExport? ReadEnvelope(string text)
    {
        if (text.Contains("\"items\"", StringComparison.Ordinal))
        {
            var envelope = JsonSerializer.Deserialize<MemoryEntryExport>(text, JsonOutput.CompactOptions);
            if (envelope is not null)
                return envelope.EnsureReadable();
        }

        var legacy = JsonSerializer.Deserialize<MemoryExport>(text, JsonOutput.CompactOptions);
        return legacy is null ? null : MemoryExportBridge.Upgrade(legacy);
    }
}
