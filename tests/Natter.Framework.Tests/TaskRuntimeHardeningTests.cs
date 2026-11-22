using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Feature.Ai;
using Natter.Infrastructure.Chat;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>S2 task-runtime hardening (docs/CODE_REVIEW_2026-11-22.md §4.2) and A21.</summary>
public sealed class TaskRuntimeHardeningTests
{
    // ------------------------------------------------------------------ S2-B1

    [Fact]
    public async Task ListActive_includes_waiting_for_input_in_both_stores()
    {
        using var temp = new TempDirectory();
        var inMemory = new InMemoryTaskStore();
        var sqlite = new SqliteTaskStore(temp.Combine("natter.db"));
        foreach (var (store, suffix) in new (ITaskStore, string)[] { (inMemory, "mem"), (sqlite, "sql") })
        {
            var task = TaskFor("waiting-" + suffix, "session-" + suffix) with { Status = AgentTaskStatus.WaitingForInput };
            await store.CreateAsync(task);
            var active = await store.ListActiveAsync(task.SessionId);
            Assert.Contains(active, item => item.TaskId == task.TaskId);
        }
    }

    // ------------------------------------------------------------------ S2-B2

    [Fact]
    public async Task Startup_validation_failure_rolls_back_and_can_be_retried()
    {
        using var temp = new TempDirectory();
        var lifecycle = new CountingLifecycle();
        var module = new StartupModule(new FailingProfileStore(), lifecycle);
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .ConfigureOptions(options =>
            {
                options.EnablePluginLoading = false;
                options.PluginConfigDirectory = temp.Combine("config");
            })
            .AddModule(module);
        await using var host = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync().AsTask());
        Assert.Equal(1, lifecycle.Starts);
        Assert.Equal(1, lifecycle.Stops);

        // The failed start must not leave _started=true; a retry runs the lifecycle again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync().AsTask());
        Assert.Equal(2, lifecycle.Starts);
        Assert.Equal(2, lifecycle.Stops);
    }

    // ------------------------------------------------------------------ S2-B3

    [Fact]
    public async Task Concurrent_task_events_get_unique_sequences()
    {
        using var temp = new TempDirectory();
        var store = new SqliteTaskStore(temp.Combine("natter.db"));
        var task = TaskFor("task-events", "session");
        await store.CreateAsync(task);

        await Task.WhenAll(Enumerable.Range(0, 30).Select(index =>
            store.AppendEventAsync(task.TaskId, TaskTraceKinds.Tool,
                TaskTracePayload.Build(("name", "clock"), ("ms", index))).AsTask()));

        var events = await store.GetEventsAsync(task.TaskId);
        Assert.Equal(31, events.Count); // created + 30 appended
        Assert.Equal(events.Select(item => item.Seq).Distinct().Count(), events.Count);
    }

    // ------------------------------------------------------------------ S2-B4

    [Fact]
    public async Task Source_with_colons_round_trips_and_delivers()
    {
        using var temp = new TempDirectory();
        var store = new SqliteTaskStore(temp.Combine("natter.db"));
        var source = new Source("discord", "discord", "bot:instance:42");
        var task = TaskFor("task-source", "session") with { Source = source };
        await store.CreateAsync(task);

        var loaded = await store.GetAsync(task.TaskId);
        Assert.NotNull(loaded);
        Assert.Equal(source, loaded!.Source);

        var recorded = new RecordingCommandDispatcher();
        var profiles = new RuntimeProfileStore(null);
        await using var dispatcher = new TaskDispatcher(
            store,
            new ImmediateWorker("done"),
            new ProfileResolver(profiles),
            profiles,
            recorded,
            new ChatCommandFactory(new TestClock()));
        await dispatcher.EnqueueAsync(loaded);
        await WaitUntilAsync(() => Task.FromResult(recorded.Commands.Count == 1));

        var command = Assert.IsType<MessageSendCommand>(recorded.Commands[0]);
        Assert.Equal(source, command.Source);
    }

    // ------------------------------------------------------------------ S2-B5

    [Fact]
    public async Task Schema_has_a_single_owner_and_prepares_foreign_keys()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("natter.db");
        _ = new SqliteConversationStore(path);
        var tasks = new SqliteTaskStore(path);
        await tasks.CreateAsync(TaskFor("task-schema", "session"));

        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            Assert.Equal(SqliteSchema.CurrentVersion, Convert.ToInt32(await version.ExecuteScalarAsync()));
        }

        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(agent_tasks);";
            await using var reader = await columns.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync())
                names.Add(reader.GetString(1));
            Assert.Contains("source_protocol", names);
            Assert.Contains("source_self_id", names);
        }

        SqliteSchema.Prepare(connection);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys;";
        Assert.Equal(1, Convert.ToInt32(await pragma.ExecuteScalarAsync()));
    }

    // ------------------------------------------------------------------ S2-B6

    [Fact]
    public async Task Router_reports_the_injected_tool_budget()
    {
        await using var host = await TestHost.CreateAsync(builder =>
            builder.AddModule(new ToolOptionsModule()));
        var router = host.Get<IChatCommandRouter>();
        var store = host.Get<ITaskStore>();
        var source = Source.Create("discord", "discord", "bot");
        var conversation = new Conversation("c1", ConversationKind.Direct);
        var user = new User("u1", "User");
        var task = TaskFor("task-budget", InMemoryConversationStore.SessionId(
            new Natter.Application.Chat.ConversationKey(source.Key, conversation.Id, null, user.Id))) with { Status = AgentTaskStatus.Running };
        await store.CreateAsync(task);

        var reply = Assert.IsType<InteractionRespondCommand>(await router.TryHandleAsync(StatusEvent(source, conversation, user, task.TaskId)));
        Assert.Contains("tools=0/7", reply.Payload.Content.Text);
    }

    private static InteractionCreatedEvent StatusEvent(Source source, Conversation conversation, User user, string taskId)
        => new()
        {
            Id = "evt:" + Guid.NewGuid().ToString("N"),
            Source = source,
            Sequence = 1,
            OccurredAt = DateTimeOffset.UtcNow,
            ReceivedAt = DateTimeOffset.UtcNow,
            Payload = new InteractionPayload(new Interaction(
                "i1",
                InteractionKind.Slash,
                user,
                conversation,
                DateTimeOffset.UtcNow,
                "status",
                new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["task"] = System.Text.Json.JsonSerializer.SerializeToElement(taskId),
                },
                null,
                "reply-token")),
        };

    // ------------------------------------------------------------------ S2-B7

    [Fact]
    public async Task Recovery_waits_for_the_transport_before_first_enqueue()
    {
        var store = new InMemoryTaskStore();
        var task = TaskFor("task-recovery", "session");
        await store.CreateAsync(task);
        var dispatcher = new RecordingTaskDispatcher();
        var registry = new FakeUpstreamRegistry();
        var services = new ServiceCollection()
            .AddSingleton<IChatUpstreamRegistry>(registry)
            .BuildServiceProvider();
        var recovery = new TaskRecoveryLifecycle(store, dispatcher);

        await recovery.StartAsync(services);
        await Task.Delay(200);
        Assert.Empty(dispatcher.Enqueued);

        registry.Register(new FakeUpstream(task.Source));
        await WaitUntilAsync(() => Task.FromResult(dispatcher.Enqueued.Count == 1));
        await recovery.StopAsync(services);
        Assert.Equal(task.TaskId, dispatcher.Enqueued[0].TaskId);
    }

    // ------------------------------------------------------------------ S2-B8

    [Fact]
    public async Task Sent_result_dedupe_set_stays_bounded()
    {
        using var temp = new TempDirectory();
        var store = new InMemoryTaskStore();
        var recorded = new RecordingCommandDispatcher();
        var profiles = new RuntimeProfileStore(null);
        await using var dispatcher = new TaskDispatcher(
            store,
            new ImmediateWorker("done"),
            new ProfileResolver(profiles),
            profiles,
            recorded,
            new ChatCommandFactory(new TestClock()),
            maxSentResults: 4);

        for (var index = 0; index < 10; index++)
        {
            var task = TaskFor("task-bound-" + index, "session");
            await store.CreateAsync(task);
            await dispatcher.EnqueueAsync(task);
            var taskId = task.TaskId;
            await WaitUntilAsync(async () => (await store.GetAsync(taskId))?.Status == AgentTaskStatus.Completed);
        }

        await WaitUntilAsync(() => Task.FromResult(recorded.Commands.Count == 10));
        Assert.Equal(0, dispatcher.TrackedCount);
        Assert.True(dispatcher.SentResultCount <= 4, $"sent dedupe set grew to {dispatcher.SentResultCount}");
    }

    // ------------------------------------------------------------------ S2-B9

    [Fact]
    public async Task Dispose_waits_for_running_worker_before_returning()
    {
        var store = new InMemoryTaskStore();
        var recorded = new RecordingCommandDispatcher();
        var profiles = new RuntimeProfileStore(null);
        var dispatcher = new TaskDispatcher(
            store,
            new ImmediateWorker("done", TimeSpan.FromMilliseconds(250), ignoreCancellation: true),
            new ProfileResolver(profiles),
            profiles,
            recorded,
            new ChatCommandFactory(new TestClock()),
            shutdownTimeout: TimeSpan.FromSeconds(5));
        var task = TaskFor("task-drain", "session");
        await store.CreateAsync(task);
        await dispatcher.EnqueueAsync(task);
        await Task.Delay(50);

        var started = Environment.TickCount64;
        await dispatcher.DisposeAsync();
        var elapsed = Environment.TickCount64 - started;

        Assert.True(elapsed >= 100, $"dispose returned too early: {elapsed}ms");
        Assert.Equal(0, dispatcher.AbandonedOnDispose);
    }

    [Fact]
    public async Task Dispose_reports_abandoned_workers_after_the_timeout()
    {
        var store = new InMemoryTaskStore();
        var recorded = new RecordingCommandDispatcher();
        var profiles = new RuntimeProfileStore(null);
        var dispatcher = new TaskDispatcher(
            store,
            new ImmediateWorker("done", TimeSpan.FromSeconds(2), ignoreCancellation: true),
            new ProfileResolver(profiles),
            profiles,
            recorded,
            new ChatCommandFactory(new TestClock()),
            shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var task = TaskFor("task-abandon", "session");
        await store.CreateAsync(task);
        await dispatcher.EnqueueAsync(task);
        await Task.Delay(50);

        await dispatcher.DisposeAsync();
        Assert.True(dispatcher.AbandonedOnDispose >= 1);
    }

    // ------------------------------------------------------------------ S2-B10

    [Fact]
    public async Task Context_pipeline_rethrows_cancellation_instead_of_degrading()
    {
        var pipeline = new HostContextPipeline(
            new InMemoryConversationStore(),
            new InMemoryMemoryStore(),
            new InMemorySummaryStore(),
            new CancellingPromptProvider(),
            new HostRecentWindowContextAssembler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.AssembleAsync(
                Source.Create("discord", "discord", "bot"),
                new Conversation("c1", ConversationKind.Direct),
                new User("u1", "User"),
                "hi",
                cancellationToken: cancellation.Token).AsTask());
    }

    // ------------------------------------------------------------------ A21

    [Fact]
    public void Shared_http_client_is_configurable()
    {
        var options = new NatterHttpClientOptions
        {
            Timeout = TimeSpan.FromSeconds(7),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            UseProxy = false,
        };

        using var handler = NatterHttpClientFactory.CreateHandler(options);
        Assert.False(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionLifetime);

        using var client = NatterHttpClientFactory.CreateClient(options);
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
        Assert.NotNull(client);
    }

    // ------------------------------------------------------------------ helpers

    private static AgentTask TaskFor(string taskId, string sessionId)
        => new()
        {
            TaskId = taskId,
            SessionId = sessionId,
            Source = Source.Create("discord", "discord", "bot"),
            ConversationId = "c1",
            ActorId = "u1",
            Prompt = "do it",
            Status = AgentTaskStatus.Pending,
        };

    private static async Task WaitUntilAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-s2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class ToolOptionsModule : ICompositionModule
    {
        public string Name => "test.tool-options";

        public void Configure(IServiceCollection services, CompositionContext context)
            => services.AddSingleton(new ToolLoopOptions { MaxToolCallsHardCap = 7 });
    }

    private sealed class CountingLifecycle : IPluginLifecycle
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            Starts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        {
            Stops++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StartupModule(IRuntimeProfileStore store, IPluginLifecycle lifecycle) : ICompositionModule
    {
        public string Name => "test.startup";

        public void Configure(IServiceCollection services, CompositionContext context)
        {
            services.AddSingleton(store);
            services.AddSingleton(lifecycle);
        }
    }

    private sealed class FailingProfileStore : IRuntimeProfileStore
    {
        private readonly RuntimeProfileSet _set = new()
        {
            IsExplicit = true,
            Profiles =
            [
                new RuntimeProfile
                {
                    Id = "tier1-default",
                    SupportTier = 1,
                    Ai = new ProfileAi { Provider = "missing#provider" },
                },
            ],
        };

        public RuntimeProfileSet Current => _set;

        public ValueTask ReloadAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ImmediateWorker(string text, TimeSpan? delay = null, bool ignoreCancellation = false) : ITaskWorker
    {
        public async ValueTask<TaskResult> ExecuteAsync(
            AgentTask task,
            TaskExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (delay is { } wait)
            {
                if (ignoreCancellation)
                    await Task.Delay(wait, CancellationToken.None);
                else
                    await Task.Delay(wait, cancellationToken);
            }

            return new TaskResult(text);
        }
    }

    private sealed class RecordingCommandDispatcher : IChatCommandDispatcher
    {
        public List<ChatCommand> Commands { get; } = [];

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
        {
            lock (Commands)
                Commands.Add(command);
            return ValueTask.FromResult(ChatAck.Success(command));
        }
    }

    private sealed class RecordingTaskDispatcher : ITaskDispatcher
    {
        public List<AgentTask> Enqueued { get; } = [];

        public ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken cancellationToken = default)
        {
            lock (Enqueued)
                Enqueued.Add(task);
            return ValueTask.FromResult(task.TaskId);
        }

        public ValueTask<bool> RequestCancelAsync(string taskId, string actorId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class FakeUpstreamRegistry : IChatUpstreamRegistry
    {
        private readonly Dictionary<Source, IChatUpstream> _upstreams = [];

        public void Register(IChatUpstream upstream)
        {
            lock (_upstreams)
                _upstreams[upstream.Source] = upstream;
        }

        public bool Unregister(Source source)
        {
            lock (_upstreams)
                return _upstreams.Remove(source);
        }

        public bool TryGet(Source source, out IChatUpstream? upstream)
        {
            lock (_upstreams)
                return _upstreams.TryGetValue(source, out upstream);
        }

        public IReadOnlyList<IChatUpstream> Upstreams
        {
            get
            {
                lock (_upstreams)
                    return _upstreams.Values.ToArray();
            }
        }
    }

    private sealed class FakeUpstream(Source source) : IChatUpstream
    {
        public Source Source { get; } = source;

        public bool IsConnected => true;

        public IObservable<ChatEvent> Events { get; } = new EmptyObservable();

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Capabilities> GetCapabilitiesAsync(Conversation? conversation = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new Capabilities());

        public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ChatAck.Success(command));

        private sealed class EmptyObservable : IObservable<ChatEvent>
        {
            public IDisposable Subscribe(IObserver<ChatEvent> observer) => new EmptyDisposable();

            private sealed class EmptyDisposable : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }

    private sealed class CancellingPromptProvider : IPromptProvider
    {
        public ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }
}
