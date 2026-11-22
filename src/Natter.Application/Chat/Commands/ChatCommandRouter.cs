using System.Globalization;
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
    ITaskDispatcher? taskDispatcher = null,
    ToolLoopOptions? toolLoopOptions = null) : IChatCommandRouter
{
    private const int TaskStatusTraceLimit = 5;
    private const int TaskListDefaultLimit = 20;
    private const int TaskListMaxLimit = 50;
    private const int TaskStatusMaxCharacters = 1500;
    private const int TaskListMaxCharacters = 3500;
    private static readonly string[] TerminalMessages = ["completed", "failed", "cancelled", "expired"];

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
        if (task is null || !IsTaskOwner(task, interaction))
            return Reply(chatEvent, interaction, "Task not found.", ephemeral: true);

        var events = await tasks.GetEventsAsync(id, cancellationToken).ConfigureAwait(false);
        var trace = events
            .Select(TaskTraceEntry.From)
            .Where(entry => IsTraceKind(entry.Kind))
            .ToArray();
        var budget = Math.Max(1, toolLoopOptions?.MaxToolCallsHardCap ?? new ToolLoopOptions().MaxToolCallsHardCap);
        var toolsUsed = trace.Count(entry => string.Equals(entry.Kind, TaskTraceKinds.Tool, StringComparison.Ordinal));
        var lastStep = trace
            .Where(entry => string.Equals(entry.Kind, TaskTraceKinds.Step, StringComparison.Ordinal))
            .Select(entry => entry.Step)
            .LastOrDefault();
        var endedAt = IsTerminal(task.Status) ? task.UpdatedAt : DateTimeOffset.UtcNow;
        var elapsedMs = Math.Max(0, (long)(endedAt - task.CreatedAt).TotalMilliseconds);

        var builder = new StringBuilder();
        builder.Append($"{task.TaskId} {task.Status} v{task.Version}");
        builder.Append($"\nelapsed={elapsedMs}ms step={(lastStep?.ToString(CultureInfo.InvariantCulture) ?? "-")}"
                       + $" tools={toolsUsed}/{budget} remaining={Math.Max(0, budget - toolsUsed)}");
        if (!string.IsNullOrWhiteSpace(task.ErrorCode))
        {
            builder.Append($"\nerror={task.ErrorCode}");
            if (!string.IsNullOrWhiteSpace(task.ErrorMessage))
                builder.Append(": ").Append(Truncate(task.ErrorMessage, 120));
        }

        if (!string.IsNullOrWhiteSpace(task.ResultText))
            builder.Append($"\nresult: {Truncate(task.ResultText, 500)}");

        builder.Append("\ntrace: ");
        if (trace.Length == 0)
        {
            builder.Append("none");
        }
        else
        {
            foreach (var entry in trace.TakeLast(TaskStatusTraceLimit))
            {
                builder.Append("\n- ").Append(entry.Kind)
                    .Append(' ').Append(entry.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(entry.Name))
                    builder.Append(' ').Append(entry.Name);
                if (!string.IsNullOrWhiteSpace(entry.Code))
                    builder.Append(' ').Append(entry.Code);
            }
        }

        return Reply(chatEvent, interaction, Truncate(builder.ToString(), TaskStatusMaxCharacters), ephemeral: true);
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
        var (statusFilter, limit, filterError) = ParseTaskListFilters(interaction);
        if (filterError is not null)
            return Reply(chatEvent, interaction, filterError, ephemeral: true);

        var active = await tasks.ListActiveAsync(InMemoryConversationStore.SessionId(key), cancellationToken).ConfigureAwait(false);
        IEnumerable<AgentTask> filtered = active;
        if (statusFilter is { } wanted)
            filtered = filtered.Where(task => StatusMatches(task.Status, wanted));

        var page = filtered.OrderBy(task => task.CreatedAt).Take(limit).ToArray();
        if (page.Length == 0)
            return Reply(chatEvent, interaction, "No active tasks.", ephemeral: true);

        var budget = Math.Max(1, toolLoopOptions?.MaxToolCallsHardCap ?? new ToolLoopOptions().MaxToolCallsHardCap);
        var rows = new List<string>(page.Length);
        foreach (var task in page)
        {
            var events = await tasks.GetEventsAsync(task.TaskId, cancellationToken).ConfigureAwait(false);
            var toolsUsed = events.Count(taskEvent => string.Equals(taskEvent.Kind, TaskTraceKinds.Tool, StringComparison.Ordinal));
            var endedAt = IsTerminal(task.Status) ? task.UpdatedAt : DateTimeOffset.UtcNow;
            var elapsedMs = Math.Max(0, (long)(endedAt - task.CreatedAt).TotalMilliseconds);
            var row = $"{task.TaskId} {task.Status} {elapsedMs}ms tools={toolsUsed}/{budget}";
            if (!string.IsNullOrWhiteSpace(task.ErrorCode))
                row += $" code={task.ErrorCode}";
            rows.Add(row);
        }

        var output = Truncate(string.Join("\n", rows), TaskListMaxCharacters);
        return Reply(chatEvent, interaction, output, ephemeral: true);
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
        var task = await tasks.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return Reply(chatEvent, interaction, "Task not found.", ephemeral: true);
        if (!IsTaskOwner(task, interaction))
            return Reply(chatEvent, interaction, "Not task owner.", ephemeral: true);

        switch (task.Status)
        {
            case AgentTaskStatus.Completed:
                return Reply(chatEvent, interaction, "Task already completed.", ephemeral: true);
            case AgentTaskStatus.Failed:
                return Reply(chatEvent, interaction, "Task already failed.", ephemeral: true);
            case AgentTaskStatus.Cancelled:
                return Reply(chatEvent, interaction, "Task already cancelled.", ephemeral: true);
            case AgentTaskStatus.Expired:
                return Reply(chatEvent, interaction, "Task already expired.", ephemeral: true);
        }

        var cancelled = await taskDispatcher.RequestCancelAsync(id, interaction.User.Id, cancellationToken).ConfigureAwait(false);
        return Reply(chatEvent, interaction,
            cancelled ? "Cancel requested." : "Cancel is not available for this state.",
            ephemeral: true);
    }

    private static bool IsTaskOwner(AgentTask task, Interaction interaction)
        => !string.IsNullOrWhiteSpace(task.ActorId)
           && string.Equals(task.ActorId, interaction.User.Id, StringComparison.Ordinal);

    private static bool IsTraceKind(string kind)
        => kind is TaskTraceKinds.Started or TaskTraceKinds.Step or TaskTraceKinds.Tool or TaskTraceKinds.Final;

    private static bool IsTerminal(AgentTaskStatus status)
        => status is AgentTaskStatus.Completed
            or AgentTaskStatus.Failed
            or AgentTaskStatus.Cancelled
            or AgentTaskStatus.Expired;

    private static (string? Status, int Limit, string? Error) ParseTaskListFilters(Interaction interaction)
    {
        string? status = null;
        if (interaction.Options is not null
            && interaction.Options.TryGetValue("status", out var statusOption)
            && statusOption.ValueKind == JsonValueKind.String)
            status = statusOption.GetString();
        if (interaction.Options is not null
            && interaction.Options.TryGetValue("limit", out var limitOption)
            && limitOption.ValueKind == JsonValueKind.Number
            && limitOption.TryGetInt32(out var optionLimit))
            return NormalizeTaskListFilters(status, optionLimit);

        var limit = TaskListDefaultLimit;
        foreach (var value in interaction.Values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var separator = value.IndexOf(':');
            if (separator <= 0)
                continue;

            var name = value[..separator].Trim();
            var argument = value[(separator + 1)..].Trim();
            if (string.Equals(name, "status", StringComparison.OrdinalIgnoreCase))
                status = argument;
            else if (string.Equals(name, "limit", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                limit = parsed;
        }

        return NormalizeTaskListFilters(status, limit);
    }

    private static (string? Status, int Limit, string? Error) NormalizeTaskListFilters(string? status, int limit)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            normalized = status.Trim().ToLowerInvariant();
            if (normalized is not ("pending" or "running" or "waiting"))
                return (null, TaskListDefaultLimit, $"Invalid status filter '{status}'. Use pending, running or waiting.");
        }

        var effective = limit <= 0 ? TaskListDefaultLimit : Math.Min(limit, TaskListMaxLimit);
        return (normalized, effective, null);
    }

    private static bool StatusMatches(AgentTaskStatus status, string filter) => filter switch
    {
        "pending" => status == AgentTaskStatus.Pending,
        "running" => status == AgentTaskStatus.Running,
        "waiting" => status == AgentTaskStatus.WaitingForInput,
        _ => true,
    };

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
