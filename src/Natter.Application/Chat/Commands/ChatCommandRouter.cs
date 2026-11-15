using System.Text;
using System.Text.Json;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Host-level UI commands handled before provider responders.</summary>
public interface IChatCommandRouter
{
    ValueTask<ChatCommand?> TryHandleAsync(
        InteractionCreatedEvent chatEvent,
        CancellationToken cancellationToken = default);
}

public sealed class ChatCommandRouter(
    IMemoryStore memories,
    ISummaryStore summaries,
    IConversationStore conversations,
    ChatCommandFactory commands,
    IContextComponentCatalog? components = null,
    IProfileMemoryStoreResolver? memoryResolver = null) : IChatCommandRouter
{
    private static readonly string[] Help =
    [
        "/ask prompt:<text>",
        "/remember text:<text> (user scope)",
        "/forget target:<id|text>",
        "/memory [limit]",
        "/reset",
    ];

    public async ValueTask<ChatCommand?> TryHandleAsync(
        InteractionCreatedEvent chatEvent,
        CancellationToken cancellationToken = default)
    {
        var interaction = chatEvent.Payload.Interaction;
        if (interaction.Kind != InteractionKind.Slash || string.IsNullOrWhiteSpace(interaction.Command))
            return null;

        return interaction.Command.Trim().ToLowerInvariant() switch
        {
            "ask" => null,
            "remember" => await RememberAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "forget" => await ForgetAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "memory" => await ListAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "reset" => await ResetAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            _ => Reply(chatEvent, interaction, "Unknown command. Available:\n" + string.Join("\n", Help), ephemeral: true),
        };
    }

    private async ValueTask<ChatCommand> RememberAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        var text = Option(interaction, "text") ?? Option(interaction, "prompt");
        if (string.IsNullOrWhiteSpace(text))
            return Reply(chatEvent, interaction, "Usage: /remember text:<text>", ephemeral: true);

        var scopeName = (Option(interaction, "scope") ?? "user").Trim().ToLowerInvariant();
        if (scopeName is not ("user" or "conversation" or "guild"))
            return Reply(chatEvent, interaction, "scope must be user, conversation, or guild.", ephemeral: true);
        if (scopeName is not "user")
        {
            return Reply(
                chatEvent,
                interaction,
                "Shared memory scopes require an administrator permission model; use scope:user for now.",
                ephemeral: true);
        }

        var item = await Store(chatEvent, interaction).WriteAsync(
            new MemoryWriteRequest(
                chatEvent.Source,
                interaction.Conversation,
                interaction.User,
                MemoryScopeType.User,
                "preference",
                text.Trim(),
                UserConfirmed: true),
            cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction, $"Remembered {item.MemoryId}.", ephemeral: true);
    }

    private async ValueTask<ChatCommand> ForgetAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        var target = Option(interaction, "target");
        if (string.IsNullOrWhiteSpace(target))
            return Reply(chatEvent, interaction, "Usage: /forget target:<id|text>", ephemeral: true);

        var byId = target.StartsWith("mem_", StringComparison.Ordinal);
        var deleted = await Store(chatEvent, interaction).DeleteAsync(
            new MemoryDeleteRequest(
                chatEvent.Source,
                interaction.Conversation,
                interaction.User,
                MemoryId: byId ? target : null,
                Text: byId ? null : target),
            cancellationToken).ConfigureAwait(false);

        var conversationKey = new ConversationKey(
            chatEvent.Source.Key,
            interaction.Conversation.Id,
            interaction.Conversation.GuildId,
            null);
        await summaries.InvalidateAsync(conversationKey, "forget", cancellationToken).ConfigureAwait(false);
        await summaries.InvalidateAsync(
            conversationKey with { ActorId = interaction.User.Id },
            "forget",
            cancellationToken).ConfigureAwait(false);
        return Reply(
            chatEvent,
            interaction,
            deleted ? "Forget successful." : "Not found, or you are not the creator.",
            ephemeral: true);
    }

    private async ValueTask<ChatCommand> ListAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        var limit = 10;
        if (Option(interaction, "limit") is { } limitText
            && int.TryParse(limitText, out var parsed))
            limit = Math.Clamp(parsed, 1, 50);

        var items = await Store(chatEvent, interaction).SearchAsync(
            new MemoryQuery(
                chatEvent.Source,
                interaction.Conversation,
                interaction.User,
                limit,
                IncludePrivate: interaction.Conversation.Kind is ConversationKind.Direct or ConversationKind.Group),
            cancellationToken).ConfigureAwait(false);
        var userItems = items.Where(item => item.ScopeType == MemoryScopeType.User).ToArray();
        if (userItems.Length == 0)
            return Reply(chatEvent, interaction, "No user memories.", ephemeral: true);

        var builder = new StringBuilder();
        foreach (var item in userItems)
            builder.AppendLine($"{item.MemoryId} [{item.ScopeType.ToString().ToLowerInvariant()}] {item.Text}");
        return Reply(chatEvent, interaction, builder.ToString(), ephemeral: true);
    }

    private async ValueTask<ChatCommand> ResetAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        var conversationKey = new ConversationKey(
            chatEvent.Source.Key,
            interaction.Conversation.Id,
            interaction.Conversation.GuildId,
            null);
        var userKey = conversationKey with { ActorId = interaction.User.Id };
        await conversations.ClearAsync(conversationKey, cancellationToken).ConfigureAwait(false);
        await conversations.ClearAsync(userKey, cancellationToken).ConfigureAwait(false);
        await summaries.InvalidateAsync(conversationKey, "reset", cancellationToken).ConfigureAwait(false);
        await summaries.InvalidateAsync(userKey, "reset", cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction, "Conversation reset.", ephemeral: true);
    }

    private IMemoryStore Store(InteractionCreatedEvent chatEvent, Interaction interaction)
    {
        if (memoryResolver is not null)
            return memoryResolver.Resolve(chatEvent.Source, interaction.Conversation, interaction.User);
        return components is not null
               && ProfileContext.Current is { } profile
               && components.TryGetMemory(profile.Memory.Store, out var selected)
               && selected is not null
            ? selected
            : memories;
    }

    private ChatCommand Reply(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        string text,
        bool ephemeral)
        => commands.Respond(chatEvent.Source, interaction, new Content(text), ephemeral);

    private static string? Option(Interaction interaction, string name)
    {
        if (interaction.Options is null || !interaction.Options.TryGetValue(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
