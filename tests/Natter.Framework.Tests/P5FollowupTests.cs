using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Infrastructure.Chat;
using Xunit;
using ConversationKey = Natter.Application.Chat.ConversationKey;

namespace Natter.Framework.Tests;

/// <summary>
/// P5 follow-up regression gaps left after the 2026-11-21 Go: <c>/status</c> <c>/tasks</c>
/// <c>/cancel</c>, <c>/ask</c> not creating tasks, lease renewal/expiry, restart idempotency,
/// the tool security matrix and Tier 2 capability degradation.
/// Report: docs/testing/2026-11-21-p5-followup-regression.md
/// </summary>
public sealed partial class P5FollowupTests
{
    // ---------------------------------------------------------------------
    // A. 命令面：/status /tasks /cancel /ask
    // ---------------------------------------------------------------------


    [Fact]
    public async Task Status_and_cancel_report_unknown_tasks_instead_of_throwing()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human();
        var store = new InMemoryTaskStore();
        await using var runtime = NewDispatcher(store, new ImmediateWorker("unused"), new RecordingDispatcher());
        var router = NewRouter(store, runtime);

        var status = await ReplyAsync(router, Slash(source, conversation, user, "status", new() { ["task"] = "task_missing" }));
        Assert.Contains("Task not found", status);

        var cancel = await ReplyAsync(router, Slash(source, conversation, user, "cancel", new() { ["task"] = "task_missing" }));
        Assert.Contains("Task not found", cancel);
        Assert.Empty(await store.ListActiveAsync("session"));
    }

    [Fact]
    public async Task Task_commands_ask_for_arguments_when_missing()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human();
        var store = new InMemoryTaskStore();
        var router = NewRouter(store, new RecordingTaskDispatcher());

        Assert.Contains("Usage: /status", await ReplyAsync(router, Slash(source, conversation, user, "status")));
        Assert.Contains("Usage: /cancel", await ReplyAsync(router, Slash(source, conversation, user, "cancel")));
        Assert.Contains("Usage: /task", await ReplyAsync(router, Slash(source, conversation, user, "task")));
        // 空 / 纯空白 prompt 也不建任务。
        Assert.Contains("Usage: /task", await ReplyAsync(router, Slash(source, conversation, user, "task", new() { ["prompt"] = "   " })));
        Assert.Empty(await store.ListActiveAsync(SessionFor(source, conversation, user)));
    }

    [Fact]
    public async Task Tasks_lists_only_the_callers_active_tasks()
    {
        var source = Discord();
        var otherSource = Source.Create("satori", "dingtalk", "dt-bot");
        var conversation = new Conversation("c1", ConversationKind.Guild, GuildId: "g1");
        var user = Human("u1");
        var otherUser = Human("u2");
        var store = new InMemoryTaskStore();
        var router = NewRouter(store, new RecordingTaskDispatcher());

        var mine = NewTask(source, SessionFor(source, conversation, user));
        var otherActor = NewTask(source, SessionFor(source, conversation, otherUser), actorId: "u2");
        var crossSource = NewTask(otherSource, SessionFor(otherSource, conversation, user));
        var finished = NewTask(source, SessionFor(source, conversation, user));
        await store.CreateAsync(mine);
        await store.CreateAsync(otherActor);
        await store.CreateAsync(crossSource);
        await store.CreateAsync(finished);
        await store.TransitionAsync(finished.TaskId, AgentTaskStatus.Running, 0);
        await store.TransitionAsync(finished.TaskId, AgentTaskStatus.Completed, 1, resultText: "old");

        var reply = await ReplyAsync(router, Slash(source, conversation, user, "tasks"));

        Assert.Contains(mine.TaskId, reply);
        Assert.DoesNotContain(otherActor.TaskId, reply);
        Assert.DoesNotContain(crossSource.TaskId, reply);
        Assert.DoesNotContain(finished.TaskId, reply);
    }

    [Fact]
    public async Task Cancel_is_refused_for_non_creators_and_terminal_tasks()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var owner = Human("u1");
        var stranger = Human("u2");
        var store = new InMemoryTaskStore();
        var recorder = new RecordingDispatcher();
        await using var runtime = NewDispatcher(store, new BlockingWorker(), recorder);
        var router = NewRouter(store, runtime);
        var task = NewTask(source, SessionFor(source, conversation, owner)) with { ActorId = owner.Id };
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);
        await WaitUntilAsync(async () => (await store.GetAsync(task.TaskId))?.Status == AgentTaskStatus.Running);

        var denied = await ReplyAsync(router, Slash(source, conversation, stranger, "cancel", new() { ["task"] = task.TaskId }));
        Assert.Equal("Not task owner.", denied);
        Assert.Equal(AgentTaskStatus.Running, (await store.GetAsync(task.TaskId))!.Status);

        var allowed = await ReplyAsync(router, Slash(source, conversation, owner, "cancel", new() { ["task"] = task.TaskId }));
        Assert.Equal("Cancel requested.", allowed);
        var cancelled = await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Cancelled } item ? item : null);
        Assert.Equal("cancelled", cancelled.ErrorCode);

        // 终态之后 cancel 只回一句话，不再改状态、不再发第二条结果。
        Assert.False(await runtime.RequestCancelAsync(task.TaskId, owner.Id));
        var afterTerminal = await ReplyAsync(router, Slash(source, conversation, owner, "cancel", new() { ["task"] = task.TaskId }));
        Assert.Equal("Task already cancelled.", afterTerminal);
        await WaitForCommandsAsync(recorder, 1);
        Assert.Single(recorder.Commands);
    }

    [Fact]
    public async Task Ask_and_non_slash_interactions_never_create_tasks()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Guild, GuildId: "g1");
        var user = Human();
        var store = new InMemoryTaskStore();
        var dispatcher = new RecordingTaskDispatcher();
        var router = NewRouter(store, dispatcher);

        Assert.Null(await router.TryHandleAsync(Slash(source, conversation, user, "ask", new() { ["prompt"] = "hello" })));
        // Tier 1 按钮/选择器交互同样不能间接造任务。
        Assert.Null(await router.TryHandleAsync(Slash(source, conversation, user, "task", new() { ["prompt"] = "x" }, InteractionKind.Button)));
        Assert.Null(await router.TryHandleAsync(Slash(source, conversation, user, "status", new() { ["task"] = "task_x" }, InteractionKind.Select)));

        Assert.Empty(await store.ListActiveAsync(SessionFor(source, conversation, user)));
        Assert.Empty(dispatcher.Enqueued);
    }

    [Fact]
    public async Task Unknown_slash_command_prints_help_without_touching_the_task_store()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human();
        var store = new InMemoryTaskStore();
        var router = NewRouter(store, new RecordingTaskDispatcher());

        var reply = await ReplyAsync(router, Slash(source, conversation, user, "nope"));

        Assert.Contains("/task prompt:<text>", reply);
        Assert.Contains("/status task:<id>", reply);
        Assert.Contains("/cancel task:<id>", reply);
        Assert.Empty(await store.ListActiveAsync(SessionFor(source, conversation, user)));
    }

    [Fact]
    public async Task Task_runtime_missing_still_replies_unavailable_for_all_four_commands()
    {
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human();
        var router = NewRouter();

        Assert.Equal("Task runtime is not available.", await ReplyAsync(router, Slash(source, conversation, user, "task", new() { ["prompt"] = "x" })));
        Assert.Equal("Task runtime is not available.", await ReplyAsync(router, Slash(source, conversation, user, "status", new() { ["task"] = "task_x" })));
        Assert.Equal("Task runtime is not available.", await ReplyAsync(router, Slash(source, conversation, user, "tasks")));
        Assert.Equal("Task runtime is not available.", await ReplyAsync(router, Slash(source, conversation, user, "cancel", new() { ["task"] = "task_x" })));
    }

    [Fact]
    public async Task Host_router_serves_status_tasks_and_cancel_after_the_wiring_fix()
    {
        // BUG-P5-ROUTER-WIRING 回归：宿主装配出来的 router 必须同时拿到 ITaskStore 与
        // ITaskDispatcher，否则 /status /tasks /cancel 会一路回 "Task runtime is not available."。
        await using var host = await TestHost.CreateAsync();
        var router = host.Get<IChatCommandRouter>();
        var store = host.Get<ITaskStore>();
        var source = Discord();
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = Human();
        var task = NewTask(source, SessionFor(source, conversation, user));
        await store.CreateAsync(task);

        var status = await ReplyAsync(router, Slash(source, conversation, user, "status", new() { ["task"] = task.TaskId }));
        Assert.Contains(task.TaskId, status);
        Assert.Contains("Pending", status);

        var list = await ReplyAsync(router, Slash(source, conversation, user, "tasks"));
        Assert.Contains(task.TaskId, list);

        var cancel = await ReplyAsync(router, Slash(source, conversation, user, "cancel", new() { ["task"] = task.TaskId }));
        Assert.Equal("Cancel requested.", cancel);
    }

    // ---------------------------------------------------------------------
    // B. lease：续租 / 过期 / 覆盖执行窗口
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Lease_renewal_keeps_the_owner_and_blocks_other_owners()
    {
        var store = new InMemoryTaskStore();
        var task = NewTask(Discord());
        await store.CreateAsync(task);

        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner-a", TimeSpan.FromMilliseconds(60)));
        // 同 owner 在有效期内续租：延长到期时间，任务不算过期。
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner-a", TimeSpan.FromMinutes(5)));
        Assert.False(await store.TryAcquireLeaseAsync(task.TaskId, "owner-b", TimeSpan.FromMinutes(5)));

        await Task.Delay(120);
        Assert.Empty(await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow));
        var leased = await store.GetAsync(task.TaskId);
        Assert.Equal("owner-a", leased!.LeaseOwner);
        Assert.True(leased.LeaseExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Expired_lease_is_swept_and_can_no_longer_be_acquired()
    {
        var store = new InMemoryTaskStore();
        var task = NewTask(Discord());
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner-a", TimeSpan.FromMilliseconds(5)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, 0);
        await Task.Delay(60);

        var expired = await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow);

        var swept = Assert.Single(expired);
        Assert.Equal(task.TaskId, swept.TaskId);
        Assert.Equal(AgentTaskStatus.Expired, swept.Status);
        Assert.Equal(2, swept.Version);
        Assert.False(await store.TryAcquireLeaseAsync(task.TaskId, "owner-b", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Dispatcher_lease_covers_the_worker_timeout_window()
    {
        // a3ab3c0 回归：LeaseTtl 必须覆盖 TaskTimeout（5m），否则 watchdog 会提前把
        // 合法长任务判过期。
        var store = new InMemoryTaskStore();
        var recorder = new RecordingDispatcher();
        await using var runtime = NewDispatcher(store, new BlockingWorker(), recorder);
        var task = NewTask(Discord());
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);
        await WaitUntilAsync(async () => (await store.GetAsync(task.TaskId))?.Status == AgentTaskStatus.Running);

        var running = (await store.GetAsync(task.TaskId))!;
        Assert.NotNull(running.LeaseExpiresAt);
        Assert.StartsWith("host:", running.LeaseOwner!);
        Assert.True(
            running.LeaseExpiresAt!.Value - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(5),
            $"lease does not cover the 5m task window: {running.LeaseExpiresAt - DateTimeOffset.UtcNow}");
        Assert.Empty(await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow));

        Assert.True(await runtime.RequestCancelAsync(task.TaskId, "u1"));
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Cancelled } item ? item : null);
    }

    [Fact]
    public async Task Sqlite_lease_survives_reopen_and_renews_for_the_same_owner()
    {
        var path = TempDatabase();
        var task = NewTask(Discord());
        var store = new SqliteTaskStore(path);
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "owner-a", TimeSpan.FromSeconds(30)));

        // 重启：新实例读回同一份 lease，别的 owner 抢不到。
        var reopened = new SqliteTaskStore(path);
        var leased = await reopened.GetAsync(task.TaskId);
        Assert.Equal("owner-a", leased!.LeaseOwner);
        Assert.NotNull(leased.LeaseExpiresAt);
        Assert.False(await reopened.TryAcquireLeaseAsync(task.TaskId, "owner-b", TimeSpan.FromSeconds(30)));

        var renewedAt = DateTimeOffset.UtcNow.AddSeconds(120);
        Assert.True(await reopened.TryAcquireLeaseAsync(task.TaskId, "owner-a", TimeSpan.FromSeconds(120)));
        var renewed = await reopened.GetAsync(task.TaskId);
        Assert.True(renewed!.LeaseExpiresAt >= renewedAt.AddSeconds(-5));
        Assert.Equal("owner-a", renewed.LeaseOwner);
    }

    [Fact]
    public async Task Sqlite_store_expires_a_running_task_after_its_lease_lapses()
    {
        var store = new SqliteTaskStore(TempDatabase());
        var task = NewTask(Discord());
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "host:crashed", TimeSpan.FromMilliseconds(5)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, 0);
        await Task.Delay(60);

        var expired = await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow);

        Assert.Equal(task.TaskId, Assert.Single(expired).TaskId);
        var swept = await store.GetAsync(task.TaskId);
        Assert.Equal(AgentTaskStatus.Expired, swept!.Status);
        Assert.False(await store.TryAcquireLeaseAsync(task.TaskId, "host:revived", TimeSpan.FromMinutes(1)));
    }

    // ---------------------------------------------------------------------
    // C. 重启幂等 + 结果只发一次
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Restart_recovery_requeues_pending_exactly_once_and_never_resends_completed()
    {
        var path = TempDatabase();
        var store = new SqliteTaskStore(path);
        var source = Discord();
        var pending = NewTask(source, "restart pending");
        var alreadyDone = NewTask(source, "done before restart");
        await store.CreateAsync(pending);
        await store.CreateAsync(alreadyDone);
        await store.TransitionAsync(alreadyDone.TaskId, AgentTaskStatus.Running, 0);
        await store.TransitionAsync(alreadyDone.TaskId, AgentTaskStatus.Completed, 1, resultText: "already sent by the old host");

        using var services = new ServiceCollection().BuildServiceProvider();
        var recorder = new RecordingDispatcher();

        // 第一次启动：只回收 Pending，Completed 的结果不重发。
        await using var first = NewDispatcher(store, new ImmediateWorker("fresh result"), recorder);
        var firstLifecycle = new TaskRecoveryLifecycle(store, first);
        await firstLifecycle.StartAsync(services);
        try
        {
            var completed = await WaitAsync(async () =>
                (await store.GetAsync(pending.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
            Assert.Equal("fresh result", completed.ResultText);
            await WaitForCommandsAsync(recorder, 1);
            var sent = Assert.Single(recorder.Commands);
            Assert.Equal($"task-result:{pending.TaskId}", sent.IdempotencyKey);
        }
        finally
        {
            await firstLifecycle.StopAsync(services);
        }

        var events = await store.GetEventsAsync(pending.TaskId);
        Assert.Equal(["created", "running", "completed"], events.Select(item => item.Kind).ToArray());

        // 第二次启动（重启）：没有可回收任务，也不重复发送。
        await using var second = NewDispatcher(store, new ImmediateWorker("second host"), recorder);
        var secondLifecycle = new TaskRecoveryLifecycle(store, second);
        await secondLifecycle.StartAsync(services);
        try
        {
            await Task.Delay(150);
            Assert.Single(recorder.Commands);
            Assert.Equal("already sent by the old host", (await store.GetAsync(alreadyDone.TaskId))!.ResultText);
            Assert.Equal("fresh result", (await store.GetAsync(pending.TaskId))!.ResultText);
        }
        finally
        {
            await secondLifecycle.StopAsync(services);
        }
    }

    [Fact]
    public async Task Cancelled_task_result_is_sent_once_and_never_replayed_after_restart()
    {
        var path = TempDatabase();
        var store = new SqliteTaskStore(path);
        var recorder = new RecordingDispatcher();
        var task = NewTask(Discord());
        await store.CreateAsync(task);

        await using (var runtime = NewDispatcher(store, new BlockingWorker(), recorder))
        {
            await runtime.EnqueueAsync(task);
            await WaitUntilAsync(async () => (await store.GetAsync(task.TaskId))?.Status == AgentTaskStatus.Running);
            Assert.True(await runtime.RequestCancelAsync(task.TaskId, "u1"));
            await WaitAsync(async () =>
                (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Cancelled } item ? item : null);
            // 结果由后台 finally 发出：等到出现再判定，避免把调度延迟当成缺陷。
            await WaitForCommandsAsync(recorder, 1);
            var current = await store.GetAsync(task.TaskId);
            Assert.True(
                recorder.Commands.Count == 1,
                $"取消后应恰好投递一条结果：status={current?.Status} v{current?.Version} "
                + $"err={current?.ErrorCode} lease={current?.LeaseOwner} cmds={recorder.Commands.Count}");
            var sent = Assert.Single(recorder.Commands);
            Assert.Contains("Task cancelled.", Assert.IsType<MessageSendCommand>(sent).Payload.Content.Text);
        }

        using var services = new ServiceCollection().BuildServiceProvider();
        var restarted = new RecordingTaskDispatcher();
        var lifecycle = new TaskRecoveryLifecycle(store, restarted);
        await lifecycle.StartAsync(services);
        try
        {
            Assert.Empty(restarted.Enqueued);
            await Task.Delay(120);
            Assert.Single(recorder.Commands);
        }
        finally
        {
            await lifecycle.StopAsync(services);
        }
    }

    [Fact]
    public async Task Recovery_expires_the_stale_running_lease_without_requeueing_it()
    {
        var path = TempDatabase();
        var store = new SqliteTaskStore(path);
        var task = NewTask(Discord());
        await store.CreateAsync(task);
        Assert.True(await store.TryAcquireLeaseAsync(task.TaskId, "host:crashed", TimeSpan.FromMilliseconds(5)));
        await store.TransitionAsync(task.TaskId, AgentTaskStatus.Running, 0);
        await Task.Delay(60);

        using var services = new ServiceCollection().BuildServiceProvider();
        var first = new RecordingTaskDispatcher();
        var firstLifecycle = new TaskRecoveryLifecycle(store, first);
        await firstLifecycle.StartAsync(services);
        try
        {
            Assert.Empty(first.Enqueued);
            Assert.Equal(AgentTaskStatus.Expired, (await store.GetAsync(task.TaskId))!.Status);
        }
        finally
        {
            await firstLifecycle.StopAsync(services);
        }

        // 再启动一次：Expired 不是可回收状态，不会二次执行。
        var second = new RecordingTaskDispatcher();
        var secondLifecycle = new TaskRecoveryLifecycle(store, second);
        await secondLifecycle.StartAsync(services);
        try
        {
            Assert.Empty(second.Enqueued);
            Assert.Equal(AgentTaskStatus.Expired, (await store.GetAsync(task.TaskId))!.Status);
        }
        finally
        {
            await secondLifecycle.StopAsync(services);
        }
    }

    // ---------------------------------------------------------------------
    // D. 工具安全矩阵
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Http_fetch_denies_by_default_and_matches_the_allowlist_exactly()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "ok");
        using var http = new HttpClient(handler);
        var deniedByDefault = new HttpFetchTool(http);
        Assert.Equal("host_not_allowed", (await deniedByDefault.ExecuteAsync(new ToolRequest("https://example.com/x"))).ErrorCode);
        Assert.Equal(0, handler.Calls);

        var allowlisted = new HttpFetchTool(http, new HashSet<string>(["example.com"], StringComparer.OrdinalIgnoreCase));
        Assert.True((await allowlisted.ExecuteAsync(new ToolRequest("https://EXAMPLE.com/x"))).Ok);
        // 精确 host 匹配：子域不会因为父域在 allowlist 里就放行。
        Assert.Equal("host_not_allowed", (await allowlisted.ExecuteAsync(new ToolRequest("https://www.example.com/x"))).ErrorCode);
        // 也不能用 URL 里嵌 allowlist host 的方式绕过。
        Assert.Equal(
            "host_not_allowed",
            (await allowlisted.ExecuteAsync(new ToolRequest("https://evil.example/?next=https://example.com"))).ErrorCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("gopher://example.com:70/_x")]
    [InlineData("//example.com/x")]
    [InlineData("example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task Http_fetch_rejects_non_https_and_relative_urls(string url)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "ok");
        using var http = new HttpClient(handler);
        var tool = new HttpFetchTool(http, new HashSet<string>(["example.com"], StringComparer.OrdinalIgnoreCase));

        var result = await tool.ExecuteAsync(new ToolRequest(url));

        Assert.Equal("invalid_url", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://127.0.0.1:8080/admin")]
    [InlineData("https://localhost/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://10.0.0.7/x")]
    [InlineData("https://192.168.1.10/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://0.0.0.0/x")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("https://2130706433/x")]
    public async Task Http_fetch_denies_loopback_private_and_metadata_hosts(string url)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "metadata");
        using var http = new HttpClient(handler);
        var tool = new HttpFetchTool(http, new HashSet<string>(["example.com"], StringComparer.OrdinalIgnoreCase));

        var result = await tool.ExecuteAsync(new ToolRequest(url));

        Assert.Equal("host_not_allowed", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Http_fetch_private_metadata_host_is_rejected_even_when_allowlisted()
    {
        // OBS-P5F-4 修复后：allowlist 之外还有私网/元数据地址门禁，即便运维误把元数据地址写进
        // NATTER_HTTP_FETCH_HOSTS 也不会真的发请求。
        var handler = new CountingHandler(HttpStatusCode.OK, "iam-credentials");
        using var http = new HttpClient(handler);
        var tool = new HttpFetchTool(http, new HashSet<string>(["169.254.169.254"], StringComparer.OrdinalIgnoreCase));

        var result = await tool.ExecuteAsync(new ToolRequest("https://169.254.169.254/latest/meta-data/iam/"));

        Assert.False(result.Ok);
        Assert.Equal("address_not_allowed", result.ErrorCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Tool_policy_decides_by_capability_not_by_name()
    {
        var policy = new ReadOnlyToolPolicy();
        var profile = new RuntimeProfile { Id = "p", Policy = new ProfilePolicy { AllowNetworkTools = false } };

        // 名叫 clock 但声明了 Shell：拒绝。
        Assert.False(policy.IsAllowed(new FakeTool("clock", ToolCapabilities.ReadOnly | ToolCapabilities.Shell), profile));
        Assert.False(policy.IsAllowed(new FakeTool("text-transform", ToolCapabilities.ReadOnly | ToolCapabilities.FileWrite), profile));
        Assert.False(policy.IsAllowed(new FakeTool("http-fetch", ToolCapabilities.ReadOnly | ToolCapabilities.Network), profile));
        // 名字不可信，能力才是判据。
        Assert.True(policy.IsAllowed(new FakeTool("anything", ToolCapabilities.ReadOnly), profile));
        Assert.Equal(3, policy.MaxCallsPerTask);
    }

    [Fact]
    public async Task No_shell_or_file_write_tool_is_registered_in_the_host()
    {
        await using var host = await TestHost.CreateAsync();
        var tools = host.Get<IEnumerable<ITool>>().ToArray();

        Assert.Equal(["clock", "http-fetch", "text-transform"], tools.Select(tool => tool.Name).OrderBy(name => name).ToArray());
        Assert.DoesNotContain(
            tools,
            tool => (tool.Capabilities & (ToolCapabilities.Shell | ToolCapabilities.FileWrite | ToolCapabilities.FileRead)) != 0);
        Assert.IsType<ReadOnlyToolPolicy>(host.Get<IToolPolicy>());
        var http = tools.Single(tool => tool.Name == "http-fetch");
        Assert.True(http.Capabilities.HasFlag(ToolCapabilities.Network));
        // 宿主装配出来的 http-fetch 用的是 NATTER_HTTP_FETCH_HOSTS，测试环境为空 → 一律拒绝。
        Assert.Equal(
            "host_not_allowed",
            (await http.ExecuteAsync(new ToolRequest("https://example.invalid/x"))).ErrorCode);
    }

    [Fact]
    public async Task Default_worker_reports_no_tool_when_network_tools_are_disabled()
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "ok");
        using var http = new HttpClient(handler);
        var worker = new DefaultTaskWorker([new HttpFetchTool(http, new HashSet<string>(["example.com"]))], new ReadOnlyToolPolicy());
        var task = NewTask(Discord(), prompt: "https://example.com/x");
        var conversation = new Conversation("c1", ConversationKind.Direct);

        var disabled = await worker.ExecuteAsync(task, new TaskExecutionContext(
            new RuntimeProfile { Id = "p" },
            conversation, [], 3, 8000, TimeSpan.FromSeconds(5)));
        Assert.Equal("no_tool", disabled.ErrorCode);
        Assert.Equal(0, handler.Calls);

        var enabled = await worker.ExecuteAsync(task, new TaskExecutionContext(
            new RuntimeProfile { Id = "p", Policy = new ProfilePolicy { AllowNetworkTools = true } },
            conversation, [], 3, 8000, TimeSpan.FromSeconds(5)));
        Assert.True(enabled.Ok);
        Assert.Contains("[http-fetch]", enabled.Text);
    }

    [Fact]
    public async Task Task_failure_logs_carry_the_task_id_but_never_the_prompt_or_secrets()
    {
        const string promptSentinel = "prompt-sentinel-6f2b1d";
        const string secretSentinel = "sk-live-never-log-this-0f9c";
        var logs = new CapturingLogger<TaskDispatcher>();
        var store = new InMemoryTaskStore();
        await using var runtime = NewDispatcher(store, new ThrowingWorker("worker exploded"), new RecordingDispatcher(), logger: logs);
        var task = NewTask(Discord(), prompt: $"{promptSentinel} {secretSentinel}");
        await store.CreateAsync(task);

        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Failed } item ? item : null);
        await Task.Delay(100);

        Assert.NotEmpty(logs.Entries);
        var text = string.Join("\n", logs.Entries);
        Assert.Contains(task.TaskId, text);
        Assert.DoesNotContain(promptSentinel, text);
        Assert.DoesNotContain(secretSentinel, text);
    }

    [Fact]
    public void Interaction_to_string_masks_the_reply_token()
    {
        var interaction = new Interaction(
            "i1",
            InteractionKind.Slash,
            Human(),
            new Conversation("c1", ConversationKind.Direct),
            DateTimeOffset.UtcNow,
            "task",
            null,
            null,
            "reply-token-sentinel-7a1");

        var text = interaction.ToString();

        Assert.DoesNotContain("reply-token-sentinel-7a1", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void Source_logging_never_interpolates_prompts_or_secrets()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        Assert.True(Directory.Exists(src), $"src 目录不存在：{src}");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (LogCallPattern().IsMatch(lines[index]) && SensitiveLogDataPattern().IsMatch(lines[index]))
                    offenders.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "日志语句不得包含 prompt / secret / token 等敏感数据：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ---------------------------------------------------------------------
    // E. Tier 2 能力降级
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Tier2_source_without_slash_support_rejects_interaction_commands()
    {
        var clock = new TestClock();
        var source = Source.Create("satori", "dingtalk", "dt-bot");
        var registry = new ChatUpstreamRegistry();
        var stub = new StubChatUpstream(source, Capabilities.TextOnly with { SlashCommands = false });
        registry.Register(stub);
        var dispatcher = new ChatCommandDispatcher(registry, new ChatIdempotencyCache(clock: () => clock.UtcNow), clock);
        var factory = new ChatCommandFactory(clock);
        var interaction = new Interaction(
            "i1", InteractionKind.Slash, Human(), new Conversation("c1", ConversationKind.Direct),
            DateTimeOffset.UtcNow, "task", null, null, "reply-token");

        var ack = await dispatcher.ExecuteAsync(factory.Respond(source, interaction, new Content("Task accepted: task_x"), ephemeral: true));

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.Unsupported, ack.Error!.Code);
        Assert.Empty(stub.Commands);
    }

    [Fact]
    public async Task Tier2_task_result_is_delivered_as_plain_text_without_interactions()
    {
        var clock = new TestClock();
        var source = Source.Create("satori", "dingtalk", "dt-bot");
        var registry = new ChatUpstreamRegistry();
        var upstream = new InMemoryChatUpstream(
            source,
            Capabilities.TextOnly with { Markdown = false, Ephemeral = false, SlashCommands = false },
            clock);
        registry.Register(upstream);
        await upstream.ConnectAsync();
        var dispatcher = new ChatCommandDispatcher(registry, new ChatIdempotencyCache(clock: () => clock.UtcNow), clock);

        var store = new InMemoryTaskStore();
        await using var runtime = NewDispatcher(store, new ImmediateWorker("任务结果：已处理"), dispatcher);
        var task = NewTask(source);
        await store.CreateAsync(task);
        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
        await Task.Delay(120);

        var sent = Assert.Single(upstream.Messages);
        Assert.Equal("任务结果：已处理", sent.Content.Text);
        Assert.Equal(ContentFormat.Plain, sent.Content.Format);
        // Tier 2 只走文本回消息：上游本身没有 slash / ephemeral 能力。
        Assert.False(upstream.Capabilities.SlashCommands);
        Assert.False(upstream.Capabilities.Ephemeral);
    }

    [Fact]
    public void Capability_degrader_drops_ephemeral_and_markdown_for_tier2()
    {
        var factory = new ChatCommandFactory(new TestClock());
        var source = Source.Create("satori", "dingtalk", "dt-bot");
        var interaction = new Interaction(
            "i1", InteractionKind.Slash, Human(), new Conversation("c1", ConversationKind.Direct),
            DateTimeOffset.UtcNow, "task", null, null, "reply-token");
        var command = factory.Respond(source, interaction, new Content("**bold** answer", ContentFormat.Markdown), ephemeral: true);

        var degraded = ChatCapabilityDegrader.DegradeRespond(
            command,
            Capabilities.TextOnly with { Markdown = false, Ephemeral = false });

        Assert.False(degraded.Payload.Ephemeral);
        Assert.Equal(ContentFormat.Plain, degraded.Payload.Content.Format);
        Assert.Equal("bold answer", degraded.Payload.Content.Text);
    }

    // ---------------------------------------------------------------------
    // F. health metrics
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Task_metrics_track_created_completed_and_active()
    {
        var metrics = new TaskMetrics();
        var store = new InMemoryTaskStore();
        var recorder = new RecordingDispatcher();
        await using var runtime = NewDispatcher(store, new ImmediateWorker("ok"), recorder, metrics);
        var task = NewTask(Discord());
        await store.CreateAsync(task);

        await runtime.EnqueueAsync(task);
        await WaitAsync(async () =>
            (await store.GetAsync(task.TaskId)) is { Status: AgentTaskStatus.Completed } item ? item : null);
        // The status transition and the metrics increment are two statements; wait for the metric
        // instead of assuming both happened, otherwise the test flakes under parallel load.
        await WaitUntilAsync(() => Task.FromResult(metrics.Snapshot().Completed == 1));

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.Created);
        Assert.Equal(1, snapshot.Completed);
        Assert.Equal(0, snapshot.Failed);
        Assert.Equal(0, snapshot.Cancelled);
        Assert.Equal(0, snapshot.Active);
    }

    // ---------------------------------------------------------------------
    // 夹具
    // ---------------------------------------------------------------------

    private static Source Discord() => Source.Create("discord", "discord", "bot");

    private static User Human(string id = "u1") => new(id, "User");

    private static string SessionFor(Source source, Conversation conversation, User user)
        => InMemoryConversationStore.SessionId(new ConversationKey(source.Key, conversation.Id, conversation.GuildId, user.Id));

    private static AgentTask NewTask(Source source, string sessionId = "session", string prompt = "do it", string actorId = "u1")
        => new()
        {
            TaskId = "task_" + Guid.NewGuid().ToString("N")[..12],
            SessionId = sessionId,
            Source = source,
            ConversationId = "c1",
            ActorId = actorId,
            Prompt = prompt,
            Status = AgentTaskStatus.Pending,
        };

    private static ChatCommandRouter NewRouter(ITaskStore? tasks = null, ITaskDispatcher? dispatcher = null)
        => new(
            new InMemoryMemoryStore(),
            new InMemorySummaryStore(),
            new InMemoryConversationStore(),
            new ChatCommandFactory(new TestClock()),
            tasks: tasks,
            taskDispatcher: dispatcher);

    private static TaskDispatcher NewDispatcher(
        ITaskStore store,
        ITaskWorker worker,
        IChatCommandDispatcher commands,
        TaskMetrics? metrics = null,
        ILogger<TaskDispatcher>? logger = null)
    {
        var profiles = new RuntimeProfileStore(null);
        return new TaskDispatcher(
            store,
            worker,
            new ProfileResolver(profiles),
            profiles,
            commands,
            new ChatCommandFactory(new TestClock()),
            metrics,
            logger);
    }

    private static InteractionCreatedEvent Slash(
        Source source,
        Conversation conversation,
        User user,
        string command,
        Dictionary<string, string>? options = null,
        InteractionKind kind = InteractionKind.Slash)
        => new()
        {
            Id = "evt:" + Guid.NewGuid().ToString("N"),
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new InteractionPayload(new Interaction(
                "i1",
                kind,
                user,
                conversation,
                DateTimeOffset.UtcNow,
                command,
                options?.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
                null,
                "reply-token")),
        };

    private static string Reply(ChatCommand? command)
        => Assert.IsType<InteractionRespondCommand>(command).Payload.Content.Text;

    private static async Task<string> ReplyAsync(IChatCommandRouter router, InteractionCreatedEvent chatEvent)
        => Reply(await router.TryHandleAsync(chatEvent));

    private static string TempDatabase()
        => Path.Combine(Path.GetTempPath(), "natter-p5followup-" + Guid.NewGuid().ToString("N"), "natter.db");

    private static async Task WaitForCommandsAsync(RecordingDispatcher recorder, int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(5));
        while (recorder.Commands.Count < expected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(20);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException("P5 follow-up probe did not become true within 15s.");
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
                return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("P5 follow-up probe did not produce a value within 15s.");
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("找不到包含 Natter.slnx 的仓库根目录。");
    }

    [GeneratedRegex(@"\.Log(Trace|Debug|Information|Warning|Error|Critical)?\s*\(")]
    private static partial Regex LogCallPattern();

    [GeneratedRegex(
        "prompt|secret|api[_-]?key|password|token|bearer|authorization",
        RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveLogDataPattern();

    private sealed class ImmediateWorker(string text) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new TaskResult(text));
    }

    private sealed class BlockingWorker : ITaskWorker
    {
        public async ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TaskResult("never");
        }
    }

    private sealed class ThrowingWorker(string message) : ITaskWorker
    {
        public ValueTask<TaskResult> ExecuteAsync(AgentTask task, TaskExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class FakeTool(string name, ToolCapabilities capabilities) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public ToolCapabilities Capabilities => capabilities;
        public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolResult(""));
    }

    private sealed class CountingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "") : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class RecordingDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }

    private sealed class RecordingTaskDispatcher : ITaskDispatcher
    {
        public List<AgentTask> Enqueued { get; } = [];

        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(task);
            return ValueTask.FromResult(task.TaskId);
        }

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);
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
