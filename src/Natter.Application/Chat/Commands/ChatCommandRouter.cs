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
    IProfileMemoryStoreResolver? memoryResolver = null,
    ITaskStore? tasks = null,
    ITaskDispatcher? taskDispatcher = null) : IChatCommandRouter
{
    private static readonly string[] Help =
    [
        "/ask prompt:<text>",
        "/remember text:<text> (user scope)",
        "/forget target:<id|text>",
        "/memory [limit]",
        "/reset",
        "/task prompt:<text>",
        "/status task:<id>",
        "/tasks",
        "/cancel task:<id>",
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
            "task" => await CreateTaskAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "tasks" => await ListTasksAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
            "cancel" => await CancelTaskAsync(chatEvent, interaction, cancellationToken).ConfigureAwait(false),
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

    private async ValueTask<ChatCommand> CreateTaskAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        if (tasks is null || taskDispatcher is null)
            return Reply(chatEvent, interaction, "Task runtime is not available.", ephemeral: true);
        var prompt = Option(interaction, "prompt") ?? Option(interaction, "text");
        if (string.IsNullOrWhiteSpace(prompt))
            return Reply(chatEvent, interaction, "Usage: /task prompt:<text>", ephemeral: true);

        var key = new ConversationKey(
            chatEvent.Source.Key,
            interaction.Conversation.Id,
            interaction.Conversation.GuildId,
            interaction.User.Id);
        var task = new AgentTask
        {
            TaskId = "task_" + Guid.NewGuid().ToString("N")[..16],
            SessionId = InMemoryConversationStore.SessionId(key),
            Source = chatEvent.Source,
            ConversationId = interaction.Conversation.Id,
            GuildId = interaction.Conversation.GuildId,
            ActorId = interaction.User.Id,
            Prompt = prompt.Trim(),
            Status = AgentTaskStatus.Pending,
        };
        await tasks.CreateAsync(task, cancellationToken).ConfigureAwait(false);
        await taskDispatcher.EnqueueAsync(task, cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction, $"Task accepted: {task.TaskId}", ephemeral: true);
    }

    private async ValueTask<ChatCommand> StatusAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        if (tasks is null)
            return Reply(chatEvent, interaction, "Task runtime is not available.", ephemeral: true);
        var id = Option(interaction, "task") ?? Option(interaction, "taskid") ?? interaction.Values?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            return Reply(chatEvent, interaction, "Usage: /status task:<task-id>", ephemeral: true);
        var task = await tasks.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return task is null
            ? Reply(chatEvent, interaction, "Task not found.", ephemeral: true)
            : Reply(chatEvent, interaction,
                $"{task.TaskId} {task.Status} v{task.Version}"
                + (string.IsNullOrWhiteSpace(task.ResultText) ? string.Empty : $"\n{Truncate(task.ResultText, 500)}"),
                ephemeral: true);
    }

    private async ValueTask<ChatCommand> ListTasksAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        if (tasks is null)
            return Reply(chatEvent, interaction, "Task runtime is not available.", ephemeral: true);
        var key = new ConversationKey(
            chatEvent.Source.Key,
            interaction.Conversation.Id,
            interaction.Conversation.GuildId,
            interaction.User.Id);
        var active = await tasks.ListActiveAsync(InMemoryConversationStore.SessionId(key), cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction,
            active.Count == 0 ? "No active tasks." : string.Join("\n", active.Select(task => $"{task.TaskId} {task.Status}")),
            ephemeral: true);
    }

    private async ValueTask<ChatCommand> CancelTaskAsync(
        InteractionCreatedEvent chatEvent,
        Interaction interaction,
        CancellationToken cancellationToken)
    {
        if (tasks is null || taskDispatcher is null)
            return Reply(chatEvent, interaction, "Task runtime is not available.", ephemeral: true);
        var id = Option(interaction, "task") ?? Option(interaction, "taskid") ?? interaction.Values?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id))
            return Reply(chatEvent, interaction, "Usage: /cancel task:<task-id>", ephemeral: true);
        var cancelled = await taskDispatcher.RequestCancelAsync(id, interaction.User.Id, cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction, cancelled ? "Cancel requested." : "Task not found, completed, or not owned by you.", ephemeral: true);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

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
