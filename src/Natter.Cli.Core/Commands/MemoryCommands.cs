using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Commands;

/// <summary>Explicit, user-confirmed memory. No model inference is performed here.</summary>
public sealed class RememberCommand(IMemoryStore store) : ICliCommandHandler
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
        var item = await store.WriteAsync(
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

public sealed class ForgetCommand(IMemoryStore store, ISummaryStore summaries) : ICliCommandHandler
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
        var deleted = await store.DeleteAsync(
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
            invocation.Console.Out.WriteLine(JsonSerializer.Serialize(new { deleted }));
        else
            invocation.Console.Out.WriteLine(deleted ? "deleted" : "not_found");
        return deleted ? CliExitCodes.Success : CliExitCodes.Failure;
    }
}

public sealed class MemoryListCommand(IMemoryStore store) : ICliCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        var context = MemoryCommandContext.Parse(invocation);
        var items = await store.SearchAsync(
            new MemoryQuery(context.Source, context.Conversation, context.User, invocation.GetIntOption("limit", 50)),
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
