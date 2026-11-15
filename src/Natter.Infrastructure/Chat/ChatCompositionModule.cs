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
        services.AddSingleton<Natter.Application.Chat.IChatResponder>(provider =>
            new Natter.Application.Chat.ProfileAwareChatResponder(
                provider.GetRequiredService<Natter.Application.Chat.IChatResponderRegistry>(),
                provider.GetRequiredService<Natter.Application.Chat.IRuntimeProfileStore>(),
                provider.GetRequiredService<Natter.Application.Chat.IProfileResolver>(),
                provider.GetRequiredService<Natter.Application.Chat.CompositeChatResponder>(),
                provider.GetService<Microsoft.Extensions.Logging.ILogger<Natter.Application.Chat.ProfileAwareChatResponder>>()));

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
