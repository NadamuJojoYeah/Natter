using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<ChatCommandFactory>();
        services.AddSingleton<Natter.Application.Chat.IConversationStore>(_ =>
            string.IsNullOrWhiteSpace(context.Options.StateDirectory)
                ? new Natter.Application.Chat.InMemoryConversationStore()
                : new SqliteConversationStore(Path.Combine(
                    Path.GetFullPath(context.Options.StateDirectory),
                    "natter.db")));
        services.AddSingleton<Natter.Application.Chat.IPromptProvider, Natter.Application.Chat.HostStaticPromptProvider>();
        services.AddSingleton<Natter.Application.Chat.IContextAssembler, Natter.Application.Chat.HostRecentWindowContextAssembler>();
        services.AddSingleton<Natter.Application.Chat.IMemoryStore>(_ =>
            string.IsNullOrWhiteSpace(context.Options.StateDirectory)
                ? new Natter.Application.Chat.InMemoryMemoryStore()
                : new SqliteMemoryStore(Path.Combine(Path.GetFullPath(context.Options.StateDirectory), "natter.db")));
        services.AddSingleton<Natter.Application.Chat.ISummaryStore>(_ =>
            string.IsNullOrWhiteSpace(context.Options.StateDirectory)
                ? new Natter.Application.Chat.InMemorySummaryStore()
                : new SqliteSummaryStore(Path.Combine(Path.GetFullPath(context.Options.StateDirectory), "natter.db")));
        services.AddSingleton<Natter.Application.Chat.ContextComponentCatalog>(provider =>
        {
            var catalog = new Natter.Application.Chat.ContextComponentCatalog();
            catalog.RegisterPrompt("host#static", provider.GetRequiredService<Natter.Application.Chat.IPromptProvider>());
            catalog.RegisterAssembler("host#recent-window", provider.GetRequiredService<Natter.Application.Chat.IContextAssembler>());
            var memory = provider.GetRequiredService<Natter.Application.Chat.IMemoryStore>();
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
                provider.GetRequiredService<Natter.Application.Chat.IMemoryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ISummaryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IPromptProvider>(),
                provider.GetRequiredService<Natter.Application.Chat.IContextAssembler>(),
                catalog);
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
            string.IsNullOrWhiteSpace(context.Options.StateDirectory)
                ? new Natter.Application.Chat.InMemoryTaskStore()
                : new SqliteTaskStore(Path.Combine(Path.GetFullPath(context.Options.StateDirectory), "natter.db")));
        services.AddSingleton<Natter.Application.Chat.ITool, Natter.Application.Chat.ClockTool>();
        services.AddSingleton<Natter.Application.Chat.IToolPolicy, Natter.Application.Chat.ReadOnlyToolPolicy>();
        services.AddSingleton<Natter.Application.Chat.ITaskWorker>(provider =>
            new Natter.Application.Chat.DefaultTaskWorker(
                provider.GetServices<Natter.Application.Chat.ITool>(),
                provider.GetRequiredService<Natter.Application.Chat.IToolPolicy>()));
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
                provider.GetRequiredService<Natter.Application.Chat.IMemoryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.ISummaryStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IConversationStore>(),
                provider.GetRequiredService<ChatCommandFactory>(),
                provider.GetRequiredService<Natter.Application.Chat.IContextComponentCatalog>(),
                provider.GetRequiredService<Natter.Application.Chat.IProfileMemoryStoreResolver>()));
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
    }
}
