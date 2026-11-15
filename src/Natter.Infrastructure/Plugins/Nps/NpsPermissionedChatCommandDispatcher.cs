using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>
/// Per-plugin dispatcher wrapper: checks chat.send/edit/delete/interactions permissions
/// before delegating to the host dispatcher.
/// </summary>
internal sealed class NpsPermissionedChatCommandDispatcher : IChatCommandDispatcher
{
    private readonly IChatCommandDispatcher _inner;
    private readonly IReadOnlySet<string> _granted;

    public NpsPermissionedChatCommandDispatcher(IChatCommandDispatcher inner, IReadOnlyList<string> granted)
    {
        _inner = inner;
        _granted = new HashSet<string>(granted, StringComparer.Ordinal);
    }

    public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        var permission = command switch
        {
            MessageSendCommand => PluginPermissions.ChatSend,
            MessageUpdateCommand => PluginPermissions.ChatEdit,
            MessageDeleteCommand => PluginPermissions.ChatDelete,
            InteractionRespondCommand => PluginPermissions.ChatInteractions,
            TypingStartCommand => PluginPermissions.ChatSend,
            _ => null,
        };

        if (permission is not null && !_granted.Contains(permission))
        {
            return ValueTask.FromResult(ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Forbidden,
                $"Plugin does not have permission '{permission}'.")));
        }

        return _inner.ExecuteAsync(command, cancellationToken);
    }
}
