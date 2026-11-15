using Microsoft.Extensions.Logging;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Provider router: profile → <c>pluginId#responderId</c> → responder. The composite
/// responder remains the fallback for hosts without an explicit profiles.json.
/// </summary>
public sealed class ProfileAwareChatResponder(
    IChatResponderRegistry registry,
    IRuntimeProfileStore profiles,
    IProfileResolver resolver,
    CompositeChatResponder fallback,
    ILogger<ProfileAwareChatResponder>? logger = null) : IChatResponder
{
    public async ValueTask<ChatCommand?> RespondAsync(
        ChatEvent chatEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        if (!TryGetContext(chatEvent, out var source, out var conversation, out var sender))
            return null;

        var set = profiles.Current;
        var profile = resolver.Resolve(source, conversation, sender);
        if (profile is null || !profile.Policy.Enabled)
            return null;

        if (!registry.TryGet(profile.Ai.Provider, out var registration) || registration is null)
        {
            if (set.IsExplicit && profile.SupportTier == 1)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' requires provider '{profile.Ai.Provider}', but no responder is loaded.");
            }

            if (set.IsExplicit && profile.SupportTier == 2)
            {
                logger?.LogWarning(
                    "Profile {ProfileId} provider {Provider} is unavailable; Tier 2 profile is disabled.",
                    profile.Id,
                    profile.Ai.Provider);
                return null;
            }

            logger?.LogWarning(
                "Profile {ProfileId} provider {Provider} is unavailable; using fallback responder routing.",
                profile.Id,
                profile.Ai.Provider);
            return await fallback.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var profileScope = ProfileContext.Push(profile);
            var responder = registration.Factory();
            var command = await responder.RespondAsync(chatEvent, cancellationToken).ConfigureAwait(false);
            if (command is not null)
            {
                logger?.LogDebug(
                    "Routed event {EventId} to profile {ProfileId} provider {Provider}",
                    chatEvent.Id,
                    profile.Id,
                    profile.Ai.Provider);
            }
            return command;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Profile {ProfileId} provider {Provider} failed for event {EventId}",
                profile.Id,
                profile.Ai.Provider,
                chatEvent.Id);
            return null;
        }
    }

    private static bool TryGetContext(
        ChatEvent chatEvent,
        out Source source,
        out Conversation conversation,
        out User sender)
    {
        switch (chatEvent)
        {
            case MessageCreatedEvent created:
                source = created.Source;
                conversation = created.Payload.Message.Conversation;
                sender = created.Payload.Message.Sender;
                return true;
            case MessageUpdatedEvent updated:
                source = updated.Source;
                conversation = updated.Payload.Message.Conversation;
                sender = updated.Payload.Message.Sender;
                return true;
            case InteractionCreatedEvent interaction:
                source = interaction.Source;
                conversation = interaction.Payload.Interaction.Conversation;
                sender = interaction.Payload.Interaction.User;
                return true;
            default:
                source = null!;
                conversation = null!;
                sender = null!;
                return false;
        }
    }
}
