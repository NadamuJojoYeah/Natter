using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

/// <summary>
/// Independent acceptance for P5.3 (task-22): the task trace written by the real worker into the real
/// InMemory/Sqlite stores, the /status /tasks /cancel UX built on it, and the new task metrics.
/// Kept separate from the implementation tests in <c>P5TaskUxTests.cs</c>.
/// Report: docs/testing/2026-11-22-p5-task-ux-acceptance.md
/// </summary>
public sealed class P5TaskUxAcceptanceTests
{
    private const string PromptSentinel = "QA_PROMPT_SENTINEL-4a1b";
    private const string ToolInputSentinel = "QA_TOOL_INPUT_SENTINEL-9c2d";
    private const string ToolOutputSentinel = "QA_TOOL_OUTPUT_SENTINEL-7e3f";
    private const string SecretSentinel = "sk-qa-secret-sentinel-5d8e";

    private static readonly string[] TraceKinds =
    [
        TaskTraceKinds.Started,
        TaskTraceKinds.Step,
        TaskTraceKinds.Tool,
        TaskTraceKinds.Final,
    ];

    // ---------------------------------------------------------------- A. trace

    [Fact]
    public async Task In_memory_trace_runs_started_step_tool_step_final_with_monotonic_seq()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var task = NewTask("task-trace", PromptSentinel);
        await store.CreateAsync(task);
        var worker = Worker(store, metrics, ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer"), new FixedTool(ToolOutputSentinel));

        var result = await worker.ExecuteAsync(task, Context(), CancellationToken.None);

        Assert.True(result.Ok, result.ErrorCode);
        var events = await store.GetEventsAsync(task.TaskId);
        var trace = events.Where(item => TraceKinds.Contains(item.Kind)).ToArray();
        Assert.Equal(
            [
                TaskTraceKinds.Started,
                TaskTraceKinds.Step,
                TaskTraceKinds.Tool,
                TaskTraceKinds.Step,
                TaskTraceKinds.Final,
            ],
            trace.Select(item => item.Kind).ToArray());

        Assert.Equal(events.Count, events.Select(item => item.Seq).Distinct().Count());
        for (var index = 1; index < events.Count; index++)
            Assert.True(events[index].Seq > events[index - 1].Seq, "seq must increase monotonically");

        var final = Payload(trace[^1]);
        Assert.Equal("completed", final.GetProperty("status").GetString());
        Assert.Equal(1, final.GetProperty("tools").GetInt32());
        Assert.Equal("clock", Payload(trace[2]).GetProperty("name").GetString());
        Assert.Equal(1, metrics.Snapshot().ToolCalls);
    }

    [Fact]
    public async Task Sqlite_trace_is_ordered_persisted_and_rejects_unsafe_payloads()
    {
        var path = TempDatabase();
        var store = new SqliteTaskStore(path);
        var task = NewTask("task-sqlite", PromptSentinel);
        await store.CreateAsync(task);
        var worker = Worker(store, new TaskMetrics(), ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer"), new FixedTool(ToolOutputSentinel));

        var result = await worker.ExecuteAsync(task, Context(), CancellationToken.None);

        Assert.True(result.Ok, result.ErrorCode);
        var reopened = new SqliteTaskStore(path);
        var trace = (await reopened.GetEventsAsync(task.TaskId))
            .Where(item => TraceKinds.Contains(item.Kind))
            .ToArray();
        Assert.Equal(
            [TaskTraceKinds.Started, TaskTraceKinds.Step, TaskTraceKinds.Tool, TaskTraceKinds.Step, TaskTraceKinds.Final],
            trace.Select(item => item.Kind).ToArray());
        Assert.Equal(trace.Length, trace.Select(item => item.Seq).Distinct().Count());
        Assert.True(trace[^1].Seq > trace[0].Seq);
        Assert.Equal("completed", Payload(trace[^1]).GetProperty("status").GetString());

        // The store itself refuses anything outside the whitelist (defense in depth).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reopened.AppendEventAsync(task.TaskId, TaskTraceKinds.Tool, """{"prompt":"leak"}""").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reopened.AppendEventAsync(task.TaskId, TaskTraceKinds.Tool, $$"""{"name":"{{ToolInputSentinel}} with spaces"}""").AsTask());
    }

    [Fact]
    public async Task Trace_store_events_status_and_task_list_never_leak_task_content()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var logs = new CapturingLogger<AiTaskWorker>();
        var task = NewTask("task-secret", $"{PromptSentinel} {SecretSentinel}");
        await store.CreateAsync(task);
        var gateway = ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer");
        var worker = Worker(store, metrics, gateway, new FixedTool($"{ToolOutputSentinel} {SecretSentinel}"), logs);
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human("u1");
        var router = Router(store, new NoopTaskDispatcher(), task.SessionId);

        var result = await worker.ExecuteAsync(task, Context(), CancellationToken.None);
        Assert.True(result.Ok, result.ErrorCode);

        var events = string.Join("\n", (await store.GetEventsAsync(task.TaskId)).Select(item => item.PayloadJson));
        var status = await ReplyAsync(router, conversation, user, "status", ("task", task.TaskId));
        var list = await ReplyAsync(router, conversation, user, "tasks");
        var logged = string.Join("\n", logs.Entries);

        foreach (var (label, text) in new[]
                 {
                     ("store events", events),
                     ("status", status),
                     ("tasks", list),
                     ("log", logged),
                 })
        {
            foreach (var sentinel in new[] { PromptSentinel, ToolInputSentinel, ToolOutputSentinel, SecretSentinel })
                Assert.False((text).Contains(sentinel, StringComparison.Ordinal), $"{label} leaked '{sentinel}'");
        }

        // Only whitelisted scalar keys survive in the stored payloads.
        var denied = new[] { "prompt", "text", "input", "output", "args", "arguments", "result", "secret", "token" };
        foreach (var item in (await store.GetEventsAsync(task.TaskId)).Where(entry => TraceKinds.Contains(entry.Kind)))
        {
            using var payload = JsonDocument.Parse(item.PayloadJson!);
            foreach (var property in payload.RootElement.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, denied);
                Assert.True(
                    property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.String
                        or JsonValueKind.True or JsonValueKind.False,
                    $"{item.Kind}/{property.Name} must be a scalar");
            }
        }
    }

    // ------------------------------------------- B. AppendEvent is best effort

    [Fact]
    public async Task Failing_append_event_keeps_the_task_successful_and_logs_no_content()
    {
        var inner = new InMemoryTaskStore();
        var store = new ThrowingTraceStore(inner);
        var logs = new CapturingLogger<AiTaskWorker>();
        var task = NewTask("task-append-fail", $"{PromptSentinel} {SecretSentinel}");
        await inner.CreateAsync(task);
        var worker = Worker(store, new TaskMetrics(), ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer"), new FixedTool(ToolOutputSentinel), logs);

        var result = await worker.ExecuteAsync(task, Context(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("answer", result.Text);
        Assert.True(store.AppendAttempts > 0);
        Assert.NotEmpty(logs.Entries);
        var logged = string.Join("\n", logs.Entries);
        Assert.Contains("task trace write failed", logged);
        Assert.Contains("InvalidOperationException", logged);
        foreach (var sentinel in new[] { PromptSentinel, ToolInputSentinel, ToolOutputSentinel, SecretSentinel })
            Assert.False(logged.Contains(sentinel, StringComparison.Ordinal), $"log leaked '{sentinel}'");
    }

    [Fact]
    public async Task Dispatched_task_completes_once_when_trace_writes_throw_and_status_degrades()
    {
        var inner = new InMemoryTaskStore();
        var store = new ThrowingTraceStore(inner);
        var metrics = new TaskMetrics();
        var recorder = new RecordingCommandDispatcher();
        var worker = Worker(store, metrics, ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer"), new FixedTool("ok"));
        await using var runtime = NewDispatcher(inner, worker, recorder, metrics);
        var task = NewTask("task-append-fail-run", PromptSentinel);
        await inner.CreateAsync(task);
        var user = Human("u1");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var router = Router(inner, runtime, task.SessionId);

        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await inner.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
        await WaitForCommandsAsync(recorder, 1);

        var reply = await ReplyAsync(router, conversation, user, "status", ("task", task.TaskId));
        Assert.Contains("Completed", reply);
        Assert.Contains("trace: none", reply);
        Assert.Single(recorder.Commands);
        Assert.Equal(1, metrics.Snapshot().Completed);
    }

    // ------------------------------------------------------------- C. /status

    [Fact]
    public async Task Status_reports_every_p53_field_for_a_real_dispatched_task()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorder = new RecordingCommandDispatcher();
        var worker = Worker(store, metrics, ScriptedGateway.CallThenAnswer(ToolInputSentinel, "final answer"), new FixedTool("tool body"));
        await using var runtime = NewDispatcher(store, worker, recorder, metrics);
        var task = NewTask("task-status", PromptSentinel);
        await store.CreateAsync(task);
        var user = Human("u1");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var router = Router(store, runtime, task.SessionId);

        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);

        var reply = await ReplyAsync(router, conversation, user, "status", ("task", task.TaskId));

        Assert.Contains(task.TaskId, reply);
        Assert.Contains("Completed", reply);
        Assert.Contains("v2", reply);
        Assert.Contains("elapsed=", reply);
        Assert.Contains("step=1", reply);
        Assert.Contains("tools=1/3", reply);
        Assert.Contains("remaining=2", reply);
        Assert.Contains("result: final answer", reply);
        Assert.Contains("trace: ", reply);
        Assert.Contains("- started", reply);
        Assert.Contains("- step", reply);
        Assert.Contains("- tool", reply);
        Assert.Contains("clock", reply);
        Assert.Contains("- final", reply);
        Assert.DoesNotContain(PromptSentinel, reply, StringComparison.Ordinal);
        Assert.True(reply.Length <= 1500, $"status reply too long: {reply.Length}");
    }

    [Fact]
    public async Task Status_truncates_a_long_result_to_500_characters()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        var task = NewTask("task-long", PromptSentinel, AgentTaskStatus.Completed) with
        {
            ResultText = new string('x', 900),
            Version = 2,
        };
        await store.CreateAsync(task);

        var reply = await ReplyAsync(router, Conversation(), Human("u1"), "status", ("task", task.TaskId));

        Assert.Contains("result: " + new string('x', 500) + "...", reply);
        Assert.DoesNotContain(new string('x', 600), reply);
        Assert.True(reply.Length < 1200, $"truncated status still too long: {reply.Length}");
    }

    [Fact]
    public async Task Status_without_trace_degrades_and_never_leaks_other_or_missing_tasks()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        await store.CreateAsync(NewTask("task-no-trace", PromptSentinel, AgentTaskStatus.Running));
        await store.CreateAsync(NewTask("task-other", $"{PromptSentinel} other", AgentTaskStatus.Running, actorId: "u2"));
        var user = Human("u1");

        var mine = await ReplyAsync(router, Conversation(), user, "status", ("task", "task-no-trace"));
        Assert.Contains("trace: none", mine);
        Assert.Contains("step=-", mine);
        Assert.Contains("tools=0/3", mine);

        var other = await ReplyAsync(router, Conversation(), user, "status", ("task", "task-other"));
        Assert.Equal("Task not found.", other);
        Assert.DoesNotContain("task-other", other, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptSentinel, other, StringComparison.Ordinal);

        var missing = await ReplyAsync(router, Conversation(), user, "status", ("task", "task-missing"));
        Assert.Equal("Task not found.", missing);
    }

    [Fact]
    public async Task Status_reports_the_error_code_and_message_for_a_failed_task()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        var task = NewTask("task-failed", PromptSentinel, AgentTaskStatus.Failed) with
        {
            ErrorCode = "tool_timeout",
            ErrorMessage = "Tool 'clock' timed out.",
        };
        await store.CreateAsync(task);

        var reply = await ReplyAsync(router, Conversation(), Human("u1"), "status", ("task", task.TaskId));

        Assert.Contains("Failed", reply);
        Assert.Contains("error=tool_timeout: Tool 'clock' timed out.", reply);
    }

    // -------------------------------------------------------------- D. /tasks

    [Fact]
    public async Task Tasks_filters_by_status_and_clamps_the_limit_to_fifty()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        var conversation = Conversation();
        var user = Human("u1");
        var session = SessionFor(conversation, user);
        for (var index = 0; index < 55; index++)
            await store.CreateAsync(NewTask($"task-p{index:00}", "p", AgentTaskStatus.Pending, sessionId: session));
        for (var index = 0; index < 4; index++)
            await store.CreateAsync(NewTask($"task-r{index:00}", "r", AgentTaskStatus.Running, sessionId: session));

        var all = await ReplyAsync(router, conversation, user, "tasks", values: ["limit:999"]);
        var lines = all.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, lines.Length);
        Assert.Equal(50, lines.Distinct().Count());

        var running = await ReplyAsync(router, conversation, user, "tasks", values: ["status:running", "limit:50"]);
        Assert.Equal(4, running.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("task-p0", running, StringComparison.Ordinal);

        var one = await ReplyAsync(router, conversation, user, "tasks", values: ["limit:1"]);
        Assert.Single(one.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Tasks_defaults_to_twenty_rows_and_guards_the_total_output_size()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        var conversation = Conversation();
        var user = Human("u1");
        var longId = "task_" + new string('a', 60);
        var session = SessionFor(conversation, user);
        for (var index = 0; index < 60; index++)
            await store.CreateAsync(NewTask(longId + index.ToString("00"), "p", AgentTaskStatus.Pending, sessionId: session));

        var defaults = await ReplyAsync(router, conversation, user, "tasks");
        Assert.Equal(20, defaults.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var guarded = await ReplyAsync(router, conversation, user, "tasks", values: ["limit:50"]);
        Assert.True(guarded.Length <= 3503, $"task list must stay bounded, got {guarded.Length}");
        Assert.True(guarded.EndsWith("...", StringComparison.Ordinal), "task list must end with the truncation marker");
    }

    [Fact]
    public async Task Tasks_reports_the_empty_message_and_rejects_an_unknown_status_filter()
    {
        var store = new InMemoryTaskStore();
        var router = Router(store, new NoopTaskDispatcher(), "session");
        var conversation = Conversation();
        var user = Human("u1");

        Assert.Equal("No active tasks.", await ReplyAsync(router, conversation, user, "tasks"));

        await store.CreateAsync(NewTask("task-p", "p", AgentTaskStatus.Pending, sessionId: SessionFor(conversation, user)));
        Assert.Equal(
            "No active tasks.",
            await ReplyAsync(router, conversation, user, "tasks", values: ["status:waiting"]));

        var invalid = await ReplyAsync(router, conversation, user, "tasks", values: ["status:bogus"]);
        Assert.Contains("Invalid status filter 'bogus'", invalid);
        Assert.Contains("pending, running or waiting", invalid);
    }

    // ------------------------------------------------------------- E. /cancel

    [Fact]
    public async Task Cancel_returns_one_distinct_message_per_state_and_permission()
    {
        var store = new InMemoryTaskStore();
        var dispatcher = new ScriptedTaskDispatcher { CancelResult = true };
        var router = Router(store, dispatcher, "session");
        var conversation = Conversation();
        var user = Human("u1");
        await store.CreateAsync(NewTask("task-completed", "p", AgentTaskStatus.Completed));
        await store.CreateAsync(NewTask("task-failed", "p", AgentTaskStatus.Failed));
        await store.CreateAsync(NewTask("task-cancelled", "p", AgentTaskStatus.Cancelled));
        await store.CreateAsync(NewTask("task-expired", "p", AgentTaskStatus.Expired));
        await store.CreateAsync(NewTask("task-running", "p", AgentTaskStatus.Running));
        await store.CreateAsync(NewTask("task-u2", "p", AgentTaskStatus.Running, actorId: "u2"));

        Assert.Equal("Task not found.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-missing")));
        Assert.Equal("Not task owner.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-u2")));
        Assert.Equal("Task already completed.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-completed")));
        Assert.Equal("Task already failed.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-failed")));
        Assert.Equal("Task already cancelled.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-cancelled")));
        Assert.Equal("Task already expired.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-expired")));
        Assert.Equal(0, dispatcher.CancelCalls);

        Assert.Equal("Cancel requested.", await ReplyAsync(router, conversation, user, "cancel", ("task", "task-running")));
        Assert.Equal(1, dispatcher.CancelCalls);

        dispatcher.CancelResult = false;
        await store.CreateAsync(NewTask("task-running-2", "p", AgentTaskStatus.Running));
        Assert.Equal(
            "Cancel is not available for this state.",
            await ReplyAsync(router, conversation, user, "cancel", ("task", "task-running-2")));
    }

    [Fact]
    public async Task Cancel_then_status_shows_cancelled_and_the_final_trace()
    {
        var store = new InMemoryTaskStore();
        var recorder = new RecordingCommandDispatcher();
        var metrics = new TaskMetrics();
        var worker = Worker(store, metrics, ScriptedGateway.Blocking(), new FixedTool("body"));
        await using var runtime = NewDispatcher(store, worker, recorder, metrics);
        var task = NewTask("task-cancel-run", $"{PromptSentinel} {SecretSentinel}");
        await store.CreateAsync(task);
        var conversation = Conversation();
        var user = Human("u1");
        var router = Router(store, runtime, task.SessionId);

        await runtime.EnqueueAsync(task);
        await WaitUntilAsync(async () => (await store.GetAsync(task.TaskId))?.Status == AgentTaskStatus.Running);

        Assert.Equal("Cancel requested.", await ReplyAsync(router, conversation, user, "cancel", ("task", task.TaskId)));
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Cancelled } item ? item : null);
        await WaitForCommandsAsync(recorder, 1);

        var reply = await ReplyAsync(router, conversation, user, "status", ("task", task.TaskId));
        Assert.Contains("Cancelled", reply);
        Assert.Contains("error=cancelled", reply);
        Assert.Contains("- final", reply);
        Assert.DoesNotContain(PromptSentinel, reply, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, reply, StringComparison.Ordinal);
        Assert.Single(recorder.Commands);
    }

    // ------------------------------------------------------------- F. metrics

    [Fact]
    public async Task Metrics_follow_a_real_task_run_and_keep_the_legacy_fields()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var recorder = new RecordingCommandDispatcher();
        var worker = Worker(store, metrics, ScriptedGateway.CallThenAnswer(ToolInputSentinel, "answer"), new FixedTool("body"));
        await using var runtime = NewDispatcher(store, worker, recorder, metrics);
        var task = NewTask("task-metrics", "prompt");
        await store.CreateAsync(task);

        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.Created);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Cancelled);
        Assert.Equal(0, snapshot.Active);
        Assert.Equal(1, snapshot.ToolCalls);
        Assert.Equal(0, snapshot.ToolTimeouts);
        Assert.Equal(1, snapshot.DurationSamples);
        Assert.True(snapshot.TotalDurationMs >= 0);
        Assert.True(snapshot.MaxDurationMs >= 0);
        Assert.Equal(snapshot.TotalDurationMs, snapshot.AverageDurationMs);
    }

    [Fact]
    public async Task Metrics_count_a_tool_timeout_and_the_task_fails_with_tool_timeout()
    {
        var store = new InMemoryTaskStore();
        var metrics = new TaskMetrics();
        var worker = new AiTaskWorker(
            Provider(store, metrics, ScriptedGateway.CallThenAnswer(ToolInputSentinel, "never")),
            [new SlowTool()],
            new ReadOnlyToolPolicy { Timeout = TimeSpan.FromMilliseconds(30) });
        var task = NewTask("task-tool-timeout", "prompt");
        await store.CreateAsync(task);

        var result = await worker.ExecuteAsync(task, Context(), CancellationToken.None);

        Assert.Equal(TaskToolLoopCodes.ToolTimeout, result.ErrorCode);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.ToolTimeouts);
        var trace = (await store.GetEventsAsync(task.TaskId)).Where(item => TraceKinds.Contains(item.Kind)).ToArray();
        Assert.Equal(TaskTraceKinds.Final, trace[^1].Kind);
        Assert.Equal("failed", Payload(trace[^1]).GetProperty("status").GetString());
        Assert.Equal(0, Payload(trace[^1]).GetProperty("tools").GetInt32());
    }

    // -------------------------------------------------------------- fixtures

    private static AiTaskWorker Worker(
        ITaskStore store,
        TaskMetrics metrics,
        ScriptedGateway gateway,
        ITool tool,
        ILogger<AiTaskWorker>? logger = null)
        => new(Provider(store, metrics, gateway, logger), [tool], new ReadOnlyToolPolicy());

    private static IServiceProvider Provider(
        ITaskStore store,
        TaskMetrics metrics,
        ScriptedGateway gateway,
        ILogger<AiTaskWorker>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(metrics);
        var responders = new ChatResponderRegistry();
        responders.Register(new ChatResponderRegistration("model", 0, "qa", () => gateway));
        services.AddSingleton<IChatResponderRegistry>(responders);
        if (logger is not null)
            services.AddSingleton(logger);
        return services.BuildServiceProvider();
    }

    private static TaskDispatcher NewDispatcher(
        ITaskStore store,
        ITaskWorker worker,
        IChatCommandDispatcher commands,
        TaskMetrics metrics)
        => new(
            store,
            worker,
            new FixedProfileResolver(),
            new RuntimeProfileStore(null),
            commands,
            new ChatCommandFactory(new TestClock()),
            metrics);

    private static ChatCommandRouter Router(ITaskStore store, ITaskDispatcher dispatcher, string sessionId)
        => new(
            new InMemoryMemoryEntryStore(),
            new InMemorySummaryStore(),
            new InMemoryConversationStore(),
            new ChatCommandFactory(new TestClock()),
            tasks: store,
            taskDispatcher: dispatcher,
            toolLoopOptions: new ToolLoopOptions());

    private static AgentTask NewTask(
        string id,
        string prompt,
        AgentTaskStatus status = AgentTaskStatus.Pending,
        string actorId = "u1",
        string sessionId = "session")
        => new()
        {
            TaskId = id,
            SessionId = sessionId,
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = actorId,
            Prompt = prompt,
            Status = status,
        };

    private static TaskExecutionContext Context()
        => new(
            new RuntimeProfile { Id = "qa", Ai = new ProfileAi { Provider = "qa#model" } },
            new Conversation("c1", ConversationKind.Direct),
            [],
            3,
            8_000,
            TimeSpan.FromSeconds(5));

    private static Conversation Conversation() => new("c1", ConversationKind.Direct);

    private static string SessionFor(Conversation conversation, User user)
        => InMemoryConversationStore.SessionId(new ConversationKey(
            "discord:discord:bot",
            conversation.Id,
            conversation.GuildId,
            user.Id));

    private static User Human(string id) => new(id, "User");

    private static JsonElement Payload(TaskEvent taskEvent)
    {
        using var document = JsonDocument.Parse(taskEvent.PayloadJson!);
        return document.RootElement.Clone();
    }

    private static string TempDatabase()
        => Path.Combine(Path.GetTempPath(), "natter-p53-acceptance-" + Guid.NewGuid().ToString("N"), "natter.db");

    private static async Task<string> ReplyAsync(
        ChatCommandRouter router,
        Conversation conversation,
        User user,
        string command,
        (string Key, string Value)? option = null,
        params string[] values)
    {
        var source = Source.Create("discord", "discord", "bot");
        var options = option is { } pair
            ? new Dictionary<string, string> { [pair.Key] = pair.Value }
            : new Dictionary<string, string>();
        var interaction = new Interaction(
            "i1",
            InteractionKind.Slash,
            user,
            conversation,
            DateTimeOffset.UtcNow,
            command,
            options.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value)),
            values.Length == 0 ? null : values.ToList(),
            "reply-token");
        var chatEvent = new InteractionCreatedEvent
        {
            Id = "evt:" + Guid.NewGuid().ToString("N"),
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new InteractionPayload(interaction),
        };

        var reply = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(chatEvent));
        return reply.Payload.Content.Text ?? string.Empty;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("probe did not become true within 10s");
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("probe produced no value within 10s");
    }

    private static async Task WaitForCommandsAsync(RecordingCommandDispatcher recorder, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (recorder.Commands.Count < expected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
    }

    private sealed class FixedProfileResolver : IProfileResolver
    {
        public RuntimeProfile? Resolve(Source source, Conversation conversation, User sender)
            => new() { Id = "qa", Ai = new ProfileAi { Provider = "qa#model" } };
    }

    private sealed class ScriptedGateway : IChatResponder, ITaskModelGateway
    {
        private readonly Queue<TaskModelReply> _script = new();

        public List<TaskToolObservation> Observations { get; } = [];

        public static ScriptedGateway Blocking()
        {
            var gateway = new ScriptedGateway();
            gateway._block = true;
            return gateway;
        }

        private bool _block;

        public static ScriptedGateway CallThenAnswer(string toolInput, string answer)
        {
            var gateway = new ScriptedGateway();
            gateway._script.Enqueue(TaskModelReply.Call("clock", toolInput));
            gateway._script.Enqueue(TaskModelReply.Answer(answer));
            return gateway;
        }

        public ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ChatCommand?>(null);

        public async ValueTask<TaskModelReply> CompleteStepAsync(TaskModelStep step, CancellationToken cancellationToken = default)
        {
            Observations.Clear();
            Observations.AddRange(step.Observations);
            if (_block)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return _script.Count > 0 ? _script.Dequeue() : TaskModelReply.Answer("final");
        }
    }

    private sealed class FixedTool(string output) : ITool
    {
        public string Name => "clock";
        public string Description => "fixed";
        public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolResult(output));
    }

    private sealed class SlowTool : ITool
    {
        public string Name => "clock";
        public string Description => "slow";
        public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

        public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new ToolResult("never");
        }
    }

    private sealed class BlockingWorker : ITaskWorker
    {
        public async ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TaskResult("never");
        }
    }

    private sealed class ThrowingTraceStore(ITaskStore inner) : ITaskStore
    {
        public int AppendAttempts { get; private set; }

        public ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken cancellationToken = default)
            => inner.CreateAsync(task, cancellationToken);

        public ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
            => inner.GetAsync(taskId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken cancellationToken = default)
            => inner.ListActiveAsync(sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListRecoverableAsync(CancellationToken cancellationToken = default)
            => inner.ListRecoverableAsync(cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
            => inner.ListRecentAsync(limit, cancellationToken);

        public ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken cancellationToken = default)
            => inner.TryAcquireLeaseAsync(taskId, owner, ttl, cancellationToken);

        public ValueTask<AgentTask> TransitionAsync(
            string taskId,
            AgentTaskStatus next,
            int expectedVersion,
            string? resultText = null,
            string? errorCode = null,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
            => inner.TransitionAsync(taskId, next, expectedVersion, resultText, errorCode, errorMessage, cancellationToken);

        public ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
            => inner.ClaimExpiredLeasesAsync(now, cancellationToken);

        public ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken cancellationToken = default)
            => inner.SaveResultAsync(taskId, resultText, cancellationToken);

        public ValueTask<IReadOnlyList<TaskEvent>> GetEventsAsync(string taskId, CancellationToken cancellationToken = default)
            => inner.GetEventsAsync(taskId, cancellationToken);

        public ValueTask AppendEventAsync(string taskId, string kind, string? payloadJson, CancellationToken cancellationToken = default)
        {
            AppendAttempts++;
            throw new InvalidOperationException("trace store down");
        }
    }

    private sealed class NoopTaskDispatcher : ITaskDispatcher
    {
        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(task.TaskId);

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class ScriptedTaskDispatcher : ITaskDispatcher
    {
        public bool CancelResult { get; set; }

        public int CancelCalls { get; private set; }

        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(task.TaskId);

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            return ValueTask.FromResult(CancelResult);
        }
    }

    private sealed class RecordingCommandDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
    }
}
