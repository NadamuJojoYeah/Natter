using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Registration helper for hosts that want the event → responder → command loop.</summary>
public static class ChatOrchestrationExtensions
{
    /// <summary>Uses the responder contribution registry (including NPS chat.responder plugins).</summary>
    public static IServiceCollection AddChatOrchestrator(
        this IServiceCollection services,
        ChatOrchestrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(options ?? new ChatOrchestrationOptions());
        services.AddSingleton<ChatOrchestrator>();
        services.AddSingleton<IChatMessageHandler>(provider => provider.GetRequiredService<ChatOrchestrator>());
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<ChatOrchestrator>());

        return services;
    }

    /// <summary>Registers one host responder into the registry and uses the composite responder.</summary>
    public static IServiceCollection AddChatOrchestrator<TResponder>(
        this IServiceCollection services,
        ChatOrchestrationOptions? options = null)
        where TResponder : class, IChatResponder
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TResponder>();
        services.AddSingleton(options ?? new ChatOrchestrationOptions());
        services.AddSingleton<ChatOrchestrator>();
        services.AddSingleton<IChatMessageHandler>(provider => provider.GetRequiredService<ChatOrchestrator>());
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<ChatOrchestrator>());
        services.AddSingleton<IChatResponder>(provider =>
        {
            var registry = provider.GetRequiredService<IChatResponderRegistry>();
            registry.Register(new ChatResponderRegistration(
                "host",
                0,
                "host",
                () => provider.GetRequiredService<TResponder>()));
            return new CompositeChatResponder(registry);
        });

        return services;
    }
}
