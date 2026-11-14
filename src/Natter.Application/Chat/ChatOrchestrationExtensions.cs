using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>Registration helper for hosts that want the event → responder → command loop.</summary>
public static class ChatOrchestrationExtensions
{
    public static IServiceCollection AddChatOrchestrator<TResponder>(
        this IServiceCollection services,
        ChatOrchestrationOptions? options = null)
        where TResponder : class, IChatResponder
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChatResponder, TResponder>();
        services.AddSingleton(options ?? new ChatOrchestrationOptions());
        services.AddSingleton<ChatOrchestrator>();
        services.AddSingleton<IChatMessageHandler>(provider => provider.GetRequiredService<ChatOrchestrator>());
        services.AddSingleton<IPluginLifecycle>(provider => provider.GetRequiredService<ChatOrchestrator>());

        return services;
    }
}
