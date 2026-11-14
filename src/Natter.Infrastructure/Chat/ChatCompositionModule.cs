using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Composition;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Chat.Safety;
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

        services.AddSingleton<ChatIngressLifecycle>();
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<ChatIngressLifecycle>());

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
