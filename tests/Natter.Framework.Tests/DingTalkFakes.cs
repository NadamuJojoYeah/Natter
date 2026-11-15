using System.Reactive.Subjects;
using Natter.Chat.DingTalk;

namespace Natter.Framework.Tests;

internal sealed class DingTalkFakeTransport : IDingTalkTransport, IDisposable
{
    private readonly Subject<DingTalkTransportEvent> _events = new();

    public IObservable<DingTalkTransportEvent> Events => _events;

    public int ConnectCount { get; private set; }

    public int DisconnectCount { get; private set; }

    public List<DingTalkReply> Replies { get; } = [];

    public DingTalkSendResult NextResult { get; set; } = DingTalkSendResult.Success();

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DingTalkSendResult> SendAsync(DingTalkReply reply, CancellationToken cancellationToken = default)
    {
        Replies.Add(reply);
        return ValueTask.FromResult(NextResult);
    }

    public void Emit(DingTalkCallbackMessage message)
        => _events.OnNext(new DingTalkTransportEvent
        {
            Kind = DingTalkTransportEventKind.Message,
            Message = message,
        });

    public void Dispose() => _events.Dispose();
}
