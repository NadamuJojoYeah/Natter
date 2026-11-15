using System.Reactive.Linq;
using System.Reactive.Subjects;
using Natter.Chat.Discord;

namespace Natter.Framework.Tests;

internal sealed class DiscordFakeTransport(string selfId = "bot") : IDiscordTransport, IDisposable
{
    private readonly Subject<DiscordTransportEvent> _events = new();

    public string SelfId { get; set; } = selfId;

    public IObservable<DiscordTransportEvent> Events => _events;

    public bool Connected { get; private set; }

    public int ConnectCount { get; private set; }

    public int DisconnectCount { get; private set; }

    public List<DiscordSendMessage> Sent { get; } = [];

    public List<DiscordUpdateMessage> Updated { get; } = [];

    public List<DiscordDeleteMessage> Deleted { get; } = [];

    public List<DiscordConversationRef> Typing { get; } = [];

    public List<DiscordInteractionResponse> Interactions { get; } = [];

    public DiscordSendResult? NextResult { get; set; }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        Connected = true;
        Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Ready,
            SelfId = SelfId,
            SelfName = "TestBot",
            GuildCount = 1,
            ChannelCount = 2,
            LatencyMs = 12,
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        Connected = false;
        Emit(new DiscordTransportEvent { Kind = DiscordTransportEventKind.Disconnected });
        return ValueTask.CompletedTask;
    }

    public ValueTask<DiscordSendResult> SendMessageAsync(DiscordSendMessage request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return ValueTask.FromResult(NextResult ?? DiscordSendResult.Success($"sent-{Sent.Count}"));
    }

    public ValueTask<DiscordSendResult> UpdateMessageAsync(DiscordUpdateMessage request, CancellationToken cancellationToken = default)
    {
        Updated.Add(request);
        return ValueTask.FromResult(NextResult ?? DiscordSendResult.Success(request.MessageId));
    }

    public ValueTask<DiscordSendResult> DeleteMessageAsync(DiscordDeleteMessage request, CancellationToken cancellationToken = default)
    {
        Deleted.Add(request);
        return ValueTask.FromResult(NextResult ?? DiscordSendResult.Success(request.MessageId));
    }

    public ValueTask TriggerTypingAsync(DiscordConversationRef conversation, CancellationToken cancellationToken = default)
    {
        Typing.Add(conversation);
        return ValueTask.CompletedTask;
    }

    public ValueTask<DiscordSendResult> RespondInteractionAsync(DiscordInteractionResponse response, CancellationToken cancellationToken = default)
    {
        Interactions.Add(response);
        return ValueTask.FromResult(NextResult ?? DiscordSendResult.Success($"interaction-{Interactions.Count}"));
    }

    public void Emit(DiscordTransportEvent transportEvent) => _events.OnNext(transportEvent);

    public void EmitMessage(DiscordMessage message, bool edit = false)
        => Emit(new DiscordTransportEvent
        {
            Kind = edit ? DiscordTransportEventKind.MessageUpdated : DiscordTransportEventKind.MessageCreated,
            Message = message,
        });

    public void EmitDeleted(string messageId, DiscordChannel channel)
        => Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.MessageDeleted,
            MessageId = messageId,
            Channel = channel,
        });

    public void EmitInteraction(DiscordInteraction interaction)
        => Emit(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.InteractionCreated,
            Interaction = interaction,
        });

    public void Dispose() => _events.Dispose();
}
