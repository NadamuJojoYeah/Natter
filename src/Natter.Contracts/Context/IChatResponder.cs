using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Feature/AI hook that turns one inbound NLMP event into an optional NLMP command.
/// Responders do not call platform APIs directly: they return a command and the
/// orchestrator dispatches it through <see cref="IChatCommandDispatcher"/>.
/// </summary>
public interface IChatResponder
{
    ValueTask<ChatCommand?> RespondAsync(ChatEvent chatEvent, CancellationToken cancellationToken = default);
}
