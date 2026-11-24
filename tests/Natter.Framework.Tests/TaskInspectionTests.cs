using Natter.Application.Chat;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Structured task inspection (task-65): the console reads persisted task events, so the mapping from
/// store rows to trace entries must be exact — kind order, codes, steps and elapsed time.
/// </summary>
public sealed class TaskInspectionTests
{
    [Fact]
    public async Task Trace_maps_persisted_events_in_seq_order_without_losing_fields()
    {
        var store = new InMemoryTaskStore();
        var task = await store.CreateAsync(NewTask("task_trace"));
        await store.AppendEventAsync(
            task.TaskId,
            TaskTraceKinds.Started,
            TaskTracePayload.Build(("status", "Running")));
        await store.AppendEventAsync(
            task.TaskId,
            TaskTraceKinds.Step,
            TaskTracePayload.Build(("step", 1), ("name", "native")));
        await store.AppendEventAsync(
            task.TaskId,
            TaskTraceKinds.Tool,
            TaskTracePayload.Build(("name", "clock"), ("status", "ok"), ("ms", 45L)));
        await store.AppendEventAsync(
            task.TaskId,
            TaskTraceKinds.Final,
            TaskTracePayload.Build(("code", "agent.timeout"), ("status", "Failed")));

        var inspection = new TaskInspectionService(store);
        var trace = await inspection.GetTraceAsync(task.TaskId);

        Assert.NotNull(trace);
        Assert.Equal("task_trace", trace!.Task.TaskId);
        Assert.Equal(["started", "step", "tool", "final"], trace.Entries.Select(entry => entry.Kind).ToArray());
        Assert.Equal(1, trace.Entries[1].Step);
        Assert.Equal("native", trace.Entries[1].Name);
        Assert.Equal("clock", trace.Entries[2].Name);
        // ① The measured duration survives the store → port mapping; the console must prefer it.
        Assert.Equal(45, trace.Entries[2].DurationMs);
        // Events without a measured duration stay null so the UI labels its fallback as an estimate.
        Assert.Null(trace.Entries[0].DurationMs);
        Assert.Null(trace.Entries[1].DurationMs);
        Assert.Equal("agent.timeout", trace.Entries[3].Code);
        Assert.True(trace.Task.ElapsedMs >= 0);
    }

    [Fact]
    public async Task Recent_tasks_are_newest_first_and_unknown_ids_return_null()
    {
        var store = new InMemoryTaskStore();
        var older = await store.CreateAsync(NewTask("task_old"));
        await Task.Delay(5);
        var newer = await store.CreateAsync(NewTask("task_new"));

        var inspection = new TaskInspectionService(store);
        var recent = await inspection.GetRecentAsync(10);

        Assert.Equal(["task_new", "task_old"], recent.Select(task => task.TaskId).ToArray());
        Assert.Null(await inspection.GetTraceAsync("task_missing"));
        Assert.Equal("task_old", older.TaskId);
        Assert.Equal("task_new", newer.TaskId);
    }

    [Fact]
    public async Task Non_trace_events_are_filtered_out_of_the_timeline()
    {
        var store = new InMemoryTaskStore();
        var task = await store.CreateAsync(NewTask("task_filter"));
        await store.AppendEventAsync(task.TaskId, TaskTraceKinds.Started, TaskTracePayload.Build(("status", "Pending")));
        await store.AppendEventAsync(task.TaskId, "lifecycle", "{\"phase\":\"claimed\"}");
        await store.AppendEventAsync(task.TaskId, TaskTraceKinds.Final, TaskTracePayload.Build(("status", "Completed")));

        var inspection = new TaskInspectionService(store);
        var trace = await inspection.GetTraceAsync(task.TaskId);

        Assert.NotNull(trace);
        Assert.Equal(["started", "final"], trace!.Entries.Select(entry => entry.Kind).ToArray());
    }

    private static AgentTask NewTask(string taskId) => new()
    {
        TaskId = taskId,
        SessionId = "session-1",
        Source = Natter.Chat.Source.Create("discord", "discord", "11"),
        ConversationId = "channel-1",
        GuildId = null,
        ActorId = "actor-1",
        Prompt = "hello",
        Status = AgentTaskStatus.Running,
    };
}
