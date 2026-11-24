using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Safety;
using Natter.Application.Chat;
using Natter.Infrastructure.Composition;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Registers the NLMP runtime: event hub, upstream registry, ingress deduplication,
/// idempotency cache, command dispatcher and command factory.
/// </summary>
public sealed class ChatCompositionModule : ICompositionModule
{
    public string Name => "Framework.Chat";

    /// <summary>
    /// Hardened handler for <see cref="Natter.Application.Chat.HttpFetchTool"/>: no automatic
    /// redirects, no egress proxy and a connect-time address check so a hostname that resolves to a
    /// private address after the tool-level check (DNS rebinding) is still refused.
    /// <para>
    /// S1-02: <see cref="SocketsHttpHandler.UseProxy"/> is fixed to <c>false</c>. With a proxy the
    /// connect callback only sees the proxy endpoint, which silently skipped the SSRF address
    /// check. The read-only fetch tool therefore never uses HTTP(S)_PROXY: the check always runs
    /// against the real target instead of being bypassed.
    /// </para>
    /// </summary>
    public static SocketsHttpHandler CreateHttpFetchHandler()
        => new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var endpoint = context.DnsEndPoint;

                IPAddress[] addresses;
                if (IPAddress.TryParse(endpoint.Host, out var literal))
                {
                    addresses = [literal];
                }
                else
                {
                    addresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
                }

                foreach (var address in addresses)
                {
                    if (Natter.Application.Chat.HttpFetchTool.IsBlockedAddress(address))
                    {
                        throw new HttpRequestException(
                            $"Refusing to connect to a private, loopback or metadata address for '{endpoint.Host}'.");
                    }
                }

                var candidate = addresses.FirstOrDefault();
                if (candidate is null)
                    throw new HttpRequestException($"No usable address for '{endpoint.Host}'.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(candidate, endpoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        var registry = context.Adapters;

        services.AddSingleton<RxChatEventHub>();
        services.AddSingleton<IChatEventSource>(provider => provider.GetRequiredService<RxChatEventHub>());
        services.AddSingleton<IChatEventPublisher>(provider => provider.GetRequiredService<RxChatEventHub>());
        services.AddSingleton<IChatEventDiagnostics>(provider => provider.GetRequiredService<RxChatEventHub>());

        services.AddSingleton(provider => new ChatEventDeduplicator(
            clock: () => provider.GetRequiredService<IClock>().UtcNow));
        services.AddSingleton<IChatEventIngress, ChatIngressProcessor>();

        services.AddSingleton<ChatUpstreamRegistry>();
        services.AddSingleton<IChatUpstreamRegistry>(provider => provider.GetRequiredService<ChatUpstreamRegistry>());

        services.AddSingleton(provider => new ChatIdempotencyCache(
            clock: () => provider.GetRequiredService<IClock>().UtcNow));
        services.AddSingleton(ChatOutboundPolicy.Default);
        services.AddSingleton<IChatCommandDispatcher, ChatCommandDispatcher>();
        services.AddSingleton<Natter.Application.Chat.MemoryRetrievalOptions>();
        services.AddSingleton<Natter.Application.Chat.MemoryRetrievalMetrics>();
        services.AddSingleton<ChatCommandFactory>();
        services.AddSingleton<Natter.Application.Chat.IConversationStore>(_ =>
            Store<Natter.Application.Chat.IConversationStore>(context.Options,
                () => new Natter.Application.Chat.InMemoryConversationStore(),
                path => new SqliteConversationStore(path)));
        // Read-only conversation projection consumed by optional plugins (Conversation Console).
        // The port has no write methods by design, the SQLite implementation never owns the schema
        // and a host without --state-dir answers empty pages instead of inventing history.
        services.AddSingleton<Natter.Application.Chat.IConversationQuery>(_ =>
            Store<Natter.Application.Chat.IConversationQuery>(context.Options,
                () => new Natter.Application.Chat.EmptyConversationQuery(),
                path => new SqliteConversationQuery(path)));
        services.AddSingleton<Natter.Application.Chat.IPromptComposer, Natter.Application.Chat.PromptComposer>();
        services.AddSingleton<Natter.Application.Chat.IPromptFragmentSource>(provider =>
            new Natter.Application.Chat.HostPromptFragmentSource(
                provider.GetRequiredService<Natter.Application.Chat.IContextComponentCatalog>(),
                provider.GetService<Microsoft.Extensions.Logging.ILogger<Natter.Application.Chat.HostPromptFragmentSource>>()));
        services.AddSingleton<Natter.Application.Chat.IPromptProvider>(provider =>
            new Natter.Application.Chat.HostStaticPromptProvider(
                provider.GetRequiredService<Natter.Application.Chat.IPromptComposer>(),
                provider.GetRequiredService<Natter.Application.Chat.IPromptFragmentSource>()));
        services.AddSingleton<Natter.Application.Chat.IContextAssembler, Natter.Application.Chat.HostRecentWindowContextAssembler>();
        // P6.7: the layered store is the production memory path. The v1 IMemoryStore stays
        // registered for one release so an operator can roll back by changing profile.memory.store,
        // but nothing on the recall path depends on it any more.
        // P6.7 rollback path: the v1 store stays registered for one release so an operator (or a
        // plugin written against the old contract) can still resolve it. Nothing on the recall path
        // depends on it — HostContextPipeline, the command router and the CLI all use the layered
        // store. Delete this registration together with the Obsolete v1 types.
        services.AddSingleton<Natter.Application.Chat.IMemoryStore>(_ =>
            Store<Natter.Application.Chat.IMemoryStore>(context.Options,
                () => new Natter.Application.Chat.InMemoryMemoryStore(),
                path => new SqliteMemoryStore(path)));
        // Migration diagnostics go to the host log once; natter health reads the same value.
        services.AddSingleton<Natter.Application.Chat.IMemoryEntryStore>(provider =>
        {
            var logger = provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                ?.CreateLogger("Natter.Memory");
            if (Natter.Application.Chat.MemoryMigrationDiagnostics.WarningSink is null)
                Natter.Application.Chat.MemoryMigrationDiagnostics.WarningSink =
                    message => logger?.Log(LogLevel.Warning, "{MemoryMigration}", message);

            return Store<Natter.Application.Chat.IMemoryEntryStore>(context.Options,
                () => new Natter.Application.Chat.InMemoryMemoryEntryStore(),
                path => new SqliteMemoryEntryStore(path));
        });
        services.AddSingleton<Natter.Application.Chat.ISummaryStore>(_ =>
            Store<Natter.Application.Chat.ISummaryStore>(context.Options,
                () => new Natter.Application.Chat.InMemorySummaryStore(),
                path => new SqliteSummaryStore(path)));
        services.AddSingleton<Natter.Application.Chat.ContextComponentCatalog>(provider =>
        {
            var catalog = new Natter.Application.Chat.ContextComponentCatalog();
            catalog.RegisterPrompt(
                "host#static",
                new Natter.Application.Chat.DeferredPromptProvider(
                    () => provider.GetRequiredService<Natter.Application.Chat.IPromptProvider>()));
            catalog.RegisterAssembler("host#recent-window", provider.GetRequiredService<Natter.Application.Chat.IContextAssembler>());
            var memory = provider.GetRequiredService<Natter.Application.Chat.IMemoryEntryStore>();
            catalog.RegisterMemory("host#inline", memory);
            // Without --state-dir the SQLite name resolves to the in-memory fallback; this keeps
            // the sample profiles runnable while making persistence explicit.
            catalog.RegisterMemory("host#sqlite", memory);
            return catalog;
        });
        services.AddSingleton<Natter.Application.Chat.IContextComponentCatalog>(provider =>
            provider.GetRequiredService<Natter.Application.Chat.ContextComponentCatalog>());
        services.AddSingleton<Natter.Application.Chat.IContextPipeline>(provider =>
        {
            var catalog = provider.GetRequiredService<Natter.Application.Chat.ContextComponentCatalog>();
            return new Natter.Application.Chat.HostContextPipeline(
                provider.GetRequiredService<Natter.Application.Chat.IConversationStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IMemoryEntryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ISummaryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IPromptProvider>(),
                provider.GetRequiredService<Natter.Application.Chat.IContextAssembler>(),
                catalog,
                provider.GetRequiredService<Natter.Application.Chat.MemoryRetrievalOptions>(),
                provider.GetRequiredService<Natter.Application.Chat.MemoryRetrievalMetrics>(),
                provider.GetService<Microsoft.Extensions.Logging.ILogger<Natter.Application.Chat.HostContextPipeline>>());
        });
        services.AddSingleton<Natter.Application.Chat.ChatResponderRegistry>();
        services.AddSingleton<Natter.Application.Chat.IChatResponderRegistry>(provider =>
            provider.GetRequiredService<Natter.Application.Chat.ChatResponderRegistry>());
        services.AddSingleton<Natter.Application.Chat.CompositeChatResponder>();
        services.AddSingleton<Natter.Application.Chat.IChatResponder>(provider =>
            provider.GetRequiredService<Natter.Application.Chat.CompositeChatResponder>());
        services.AddSingleton<Natter.Application.Chat.IRuntimeProfileStore>(_ =>
            new Natter.Application.Chat.RuntimeProfileStore(
                context.Options.PluginConfigDirectory,
                context.Options.ProfileOverride));
        services.AddSingleton<Natter.Application.Chat.IProfileResolver, Natter.Application.Chat.ProfileResolver>();
        services.AddSingleton<Natter.Application.Chat.IProfileMemoryStoreResolver, Natter.Application.Chat.ProfileMemoryStoreResolver>();
        services.AddSingleton<Natter.Application.Chat.HostRuntimeClock>();
        services.AddSingleton<Natter.Application.Chat.TaskMetrics>();
        services.AddSingleton<Natter.Application.Chat.ITaskStore>(_ =>
            Store<Natter.Application.Chat.ITaskStore>(context.Options,
                () => new Natter.Application.Chat.InMemoryTaskStore(),
                path => new SqliteTaskStore(path)));
        // task-65: structured task inspection for the consoles (read-only; never mutates tasks).
        services.AddSingleton<Natter.Application.Chat.IAgentTaskInspection>(provider =>
            new TaskInspectionService(
                provider.GetRequiredService<Natter.Application.Chat.ITaskStore>(),
                // TimeProvider is optional in stripped-down hosts (tests, embedded plugins).
                provider.GetService<TimeProvider>()));
        services.AddSingleton<Natter.Application.Chat.ITool, Natter.Application.Chat.ClockTool>();
        services.AddSingleton<Natter.Application.Chat.ITool, Natter.Application.Chat.TextTransformTool>();
        // A21: configurable handler/factory instead of a bare singleton HttpClient. Pooled
        // connections are recycled so DNS changes are picked up, and proxy/timeout settings are
        // explicit (the S1-02 fetch handler keeps UseProxy=false for its own hardened client).
        services.AddSingleton(new Natter.Infrastructure.Chat.NatterHttpClientOptions());
        services.AddSingleton<HttpClient>(provider =>
            Natter.Infrastructure.Chat.NatterHttpClientFactory.CreateClient(
                provider.GetRequiredService<Natter.Infrastructure.Chat.NatterHttpClientOptions>()));
        services.AddSingleton<Natter.Application.Chat.ITool>(provider =>
        {
            var hosts = (Environment.GetEnvironmentVariable("NATTER_HTTP_FETCH_HOSTS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // OBS-P5F-4: the fetch tool gets its own hardened client. It never follows redirects and
            // validates the address actually used for the socket connection (DNS rebinding defence),
            // while the shared HttpClient used by AI providers keeps its existing behaviour.
            var hardened = new HttpClient(CreateHttpFetchHandler(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(20),
            };
            return new Natter.Application.Chat.HttpFetchTool(hardened, hosts);
        });
        services.AddSingleton<Natter.Application.Chat.IToolPolicy, Natter.Application.Chat.ReadOnlyToolPolicy>();
        services.AddSingleton(new Natter.Application.Chat.ToolLoopOptions());
        // P5.2: one registry/invoker for every tool call (native function calling and the text
        // fallback both execute through it: policy, permissions, schema, timeout, output bounds).
        services.AddSingleton<Natter.Application.Chat.IToolRegistry>(provider =>
            new Natter.Application.Chat.ToolRegistry(
                provider.GetServices<Natter.Application.Chat.ITool>(),
                Natter.Application.Chat.ToolProtocolCapabilities.NativeAndText));
        services.AddSingleton<Natter.Application.Chat.ToolInvoker>(provider =>
            new Natter.Application.Chat.ToolInvoker(
                provider.GetRequiredService<Natter.Application.Chat.IToolRegistry>(),
                provider.GetRequiredService<Natter.Application.Chat.IToolPolicy>()));
        services.AddSingleton<Natter.Application.Chat.ITaskWorker>(provider =>
            new Natter.Application.Chat.AiTaskWorker(
                provider,
                provider.GetServices<Natter.Application.Chat.ITool>(),
                provider.GetRequiredService<Natter.Application.Chat.IToolPolicy>(),
                provider.GetService<Natter.Application.Chat.ToolLoopOptions>(),
                provider.GetRequiredService<Natter.Application.Chat.IToolRegistry>(),
                provider.GetRequiredService<Natter.Application.Chat.ToolInvoker>()));
        services.AddSingleton<Natter.Abstractions.Plugins.IPluginLifecycle, Natter.Application.Chat.TaskRecoveryLifecycle>();
        services.AddSingleton<Natter.Application.Chat.ITaskDispatcher>(provider =>
            new Natter.Application.Chat.TaskDispatcher(
                provider.GetRequiredService<Natter.Application.Chat.ITaskStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ITaskWorker>(),
                provider.GetRequiredService<Natter.Application.Chat.IProfileResolver>(),
                provider.GetRequiredService<Natter.Application.Chat.IRuntimeProfileStore>(),
                provider.GetRequiredService<IChatCommandDispatcher>(),
                provider.GetRequiredService<ChatCommandFactory>(),
                provider.GetRequiredService<Natter.Application.Chat.TaskMetrics>()));
        services.AddSingleton<Natter.Application.Chat.IChatCommandRouter>(provider =>
            new Natter.Application.Chat.ChatCommandRouter(
                provider.GetRequiredService<Natter.Application.Chat.IMemoryEntryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ISummaryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IConversationStore>(),
                provider.GetRequiredService<ChatCommandFactory>(),
                provider.GetRequiredService<Natter.Application.Chat.IContextComponentCatalog>(),
                provider.GetRequiredService<Natter.Application.Chat.IProfileMemoryStoreResolver>(),
                provider.GetRequiredService<Natter.Application.Chat.ITaskStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ITaskDispatcher>(),
                // S2-B6: the same ToolLoopOptions instance the worker uses, so /status and /tasks
                // report the actual tool budget instead of a default.
                provider.GetRequiredService<Natter.Application.Chat.ToolLoopOptions>()));
        services.AddSingleton<Natter.Application.Chat.IChatResponder>(provider =>
            new Natter.Application.Chat.ProfileAwareChatResponder(
                provider.GetRequiredService<Natter.Application.Chat.IChatResponderRegistry>(),
                provider.GetRequiredService<Natter.Application.Chat.IRuntimeProfileStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IProfileResolver>(),
                provider.GetRequiredService<Natter.Application.Chat.CompositeChatResponder>(),
                provider.GetService<Microsoft.Extensions.Logging.ILogger<Natter.Application.Chat.ProfileAwareChatResponder>>(),
                provider.GetService<Natter.Application.Chat.IChatCommandRouter>()));

        services.AddSingleton<ChatIngressLifecycle>();
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<ChatIngressLifecycle>());

        // The responder loop is part of the host runtime, not an optional CLI command:
        // without it, chat.responder plugins would never be invoked.
        services.AddChatOrchestrator();

        registry.Register(new PortAdapterDescriptor(
            nameof(IChatEventSource),
            nameof(RxChatEventHub),
            ServiceLifetime.Singleton,
            "framework:chat",
            "Hot Rx NLMP event stream with subscriber isolation."));

        registry.Register(new PortAdapterDescriptor(
            nameof(IChatUpstreamRegistry),
            nameof(ChatUpstreamRegistry),
            ServiceLifetime.Singleton,
            "framework:chat",
            "Source-keyed upstream registry."));

        registry.Register(new PortAdapterDescriptor(
            nameof(IChatCommandDispatcher),
            nameof(ChatCommandDispatcher),
            ServiceLifetime.Singleton,
            "framework:chat",
            "Validates, degrades, chunks and dispatches NLMP commands."));

        registry.Register(new PortAdapterDescriptor(
            nameof(IChatEventIngress),
            nameof(ChatIngressProcessor),
            ServiceLifetime.Singleton,
            "framework:chat",
            "Replay-window event deduplication."));

        registry.Register(new PortAdapterDescriptor(
            nameof(Natter.Application.Chat.IConversationQuery),
            nameof(SqliteConversationQuery),
            ServiceLifetime.Singleton,
            "framework:chat",
            "Read-only conversation projection (list/search/messages) for optional console plugins."));
    }

    /// <summary>File name of the host database. Changing it changes it for every store at once.</summary>
    internal const string DatabaseFileName = "natter.db";

    /// <summary>
    /// Absolute path of the host database, or <see langword="null"/> when no state directory was
    /// configured (then every store falls back to its in-memory implementation).
    /// <para>
    /// Single source of truth on purpose: six stores share this one file, so a second copy of this
    /// expression would let a rename of the database file silently split storage in two.
    /// </para>
    /// </summary>
    internal static string? ResolveDatabasePath(CompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.IsNullOrWhiteSpace(options.StateDirectory)
            ? null
            : Path.Combine(Path.GetFullPath(options.StateDirectory), DatabaseFileName);
    }

    /// <summary>
    /// Picks the in-memory or the persistent implementation from the presence of a state directory.
    /// Keeps every store registration to one line and one decision.
    /// </summary>
    internal static T Store<T>(
        CompositionOptions options,
        Func<T> inMemory,
        Func<string, T> persistent)
    {
        var databasePath = ResolveDatabasePath(options);

        return databasePath is null ? inMemory() : persistent(databasePath);
    }
}
