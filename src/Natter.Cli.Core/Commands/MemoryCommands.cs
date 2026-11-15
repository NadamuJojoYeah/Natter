using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Explicit, user-confirmed memory. No model inference is performed here.</summary>
public sealed class RememberCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var text = string.Join(' ', invocation.Positionals);
        if (string.IsNullOrWhiteSpace(text))
        {
            invocation.Console.Error.WriteLine("usage: remember <text> [--scope user|conversation|guild|global]");
            return CliExitCodes.Usage;
        }

        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var item = await selectedStore.WriteAsync(
            new MemoryWriteRequest(
                context.Source,
                context.Conversation,
                context.User,
                context.Scope,
                invocation.GetOption("kind", "preference"),
                text,
                Confidence: 1.0,
                Sensitivity: invocation.GetOption("sensitivity", "normal"),
                UserConfirmed: true),
            cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
        else
            invocation.Console.Out.WriteLine($"remembered {item.MemoryId} scope={item.ScopeType.ToString().ToLowerInvariant()}");

        return CliExitCodes.Success;
    }
}

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
        var deleted = await selectedStore.DeleteAsync(
            new MemoryDeleteRequest(context.Source, context.Conversation, context.User,
                MemoryId: value.StartsWith("mem_", StringComparison.Ordinal) ? value : null,
                Text: value.StartsWith("mem_", StringComparison.Ordinal) ? null : value),
            cancellationToken).ConfigureAwait(false);

        var conversationKey = new Natter.Application.Chat.ConversationKey(
            context.Source.Key, context.Conversation.Id, context.Conversation.GuildId, null);
        var userKey = conversationKey with { ActorId = context.User.Id };
        await summaries.InvalidateAsync(conversationKey, "forget", cancellationToken).ConfigureAwait(false);
        await summaries.InvalidateAsync(userKey, "forget", cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                deleted,
                reason = deleted ? null : "not found or caller is not the creator",
            }));
        }
        else
        {
            invocation.Console.Out.WriteLine(
                deleted ? "deleted" : "deleted:false (not found or caller is not the creator)");
        }
        return deleted ? CliExitCodes.Success : CliExitCodes.Failure;
    }
}

public sealed class MemoryListCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var items = await selectedStore.SearchAsync(
            new MemoryQuery(
                context.Source,
                context.Conversation,
                context.User,
                invocation.GetIntOption("limit", 50),
                IncludePrivate: context.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group),
            cancellationToken).ConfigureAwait(false);

        if (invocation.HasFlag("json"))
        {
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
            return CliExitCodes.Success;
        }

        foreach (var item in items)
            invocation.Console.Out.WriteLine($"{item.MemoryId} [{item.ScopeType.ToString().ToLowerInvariant()}] {item.Text}");
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
            "global" => MemoryScopeType.Global,
            _ => MemoryScopeType.User,
        };
        return new MemoryCommandContext(source, conversation, user, scope);
    }
}

/// <summary>Exports visible memory as a versioned envelope (memory only, no summary text).</summary>
public sealed class MemoryExportCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var context = MemoryCommandContext.Parse(invocation);
        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var export = await selectedStore.ExportAsync(
            new MemoryExportRequest(context.Source, context.Conversation, context.User),
            cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(export, JsonOptions);
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
            invocation.Console.Out.WriteLine($"exported {export.Items.Count} memory item(s) to {path}");
        }
        else
        {
            invocation.Console.Out.WriteLine(json);
        }
        return CliExitCodes.Success;
    }
}

/// <summary>Imports a memory envelope; only current scope/source/actor-visible items are accepted.</summary>
public sealed class MemoryImportCommand(IProfileMemoryStoreResolver resolver) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var file = invocation.GetOption("file");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            invocation.Console.Error.WriteLine("usage: memory import --file <json> --source ... --actor ...");
            return CliExitCodes.Usage;
        }

        MemoryExport? export;
        try
        {
            export = JsonSerializer.Deserialize<MemoryExport>(await File.ReadAllTextAsync(file, cancellationToken), JsonOptions);
        }
        catch (JsonException ex)
        {
            invocation.Console.Error.WriteLine($"invalid memory export: {ex.Message}");
            return CliExitCodes.Failure;
        }

        if (export is null || export.SchemaVersion != 1)
        {
            invocation.Console.Error.WriteLine("unsupported memory export schemaVersion");
            return CliExitCodes.Failure;
        }

        var context = MemoryCommandContext.Parse(invocation);
        var mismatch = export.Items.Where(item =>
            !Natter.Application.Chat.MemoryScope.IsVisible(
                item,
                context.Source,
                context.Conversation,
                context.User,
                includePrivate: context.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group)).ToArray();
        if (mismatch.Length > 0)
        {
            invocation.Console.Error.WriteLine($"refusing import: {mismatch.Length} item(s) do not belong to the requested source/scope/actor");
            return CliExitCodes.Failure;
        }

        var selectedStore = resolver.Resolve(context.Source, context.Conversation, context.User);
        var imported = await selectedStore.ImportAsync(
            new MemoryImportRequest(
                context.Source,
                context.Conversation,
                context.User,
                export,
                Overwrite: invocation.HasFlag("overwrite")),
            cancellationToken).ConfigureAwait(false);
        if (invocation.HasFlag("json"))
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(new { imported, skipped = export.Items.Count - imported }));
        else
            invocation.Console.Out.WriteLine($"imported {imported} memory item(s)");
        return CliExitCodes.Success;
    }
}
