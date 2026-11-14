using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Ports;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Composition;

namespace Natter.Framework.Tests;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 14, 14, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

internal sealed class StringConsole : IConsole
{
    public StringWriter OutWriter { get; } = new();

    public StringWriter ErrorWriter { get; } = new();

    public TextWriter Out => OutWriter;

    public TextWriter Error => ErrorWriter;

    public bool IsInteractive => false;

    public string? ReadLine() => null;

    public string Output => OutWriter.ToString();

    public string ErrorText => ErrorWriter.ToString();
}

internal sealed class CaptureObserver<T> : IObserver<T>
{
    private readonly List<T> _items;

    public CaptureObserver(List<T> items) => _items = items;

    public void OnCompleted()
    {
    }

    public void OnError(Exception error) => throw error;

    public void OnNext(T value) => _items.Add(value);
}

internal sealed class EmptyObservable<T> : IObservable<T>
{
    public static EmptyObservable<T> Instance { get; } = new();

    public IDisposable Subscribe(IObserver<T> observer) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>Upstream stub that records commands and returns scripted acks.</summary>
internal sealed class StubChatUpstream : IChatUpstream
{
    public StubChatUpstream(Source source, Capabilities? capabilities = null)
    {
        Source = source;
        Capabilities = capabilities ?? Capabilities.TextOnly;
    }

    public Source Source { get; }

    public Capabilities Capabilities { get; set; }

    public bool IsConnected { get; set; } = true;

    public ChatAck? NextAck { get; set; }

    public Func<ChatCommand, int, ChatAck>? AckFactory { get; set; }

    public List<ChatCommand> Commands { get; } = [];

    public IObservable<ChatEvent> Events => EmptyObservable<ChatEvent>.Instance;

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(
        Conversation? conversation = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Capabilities);

    public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);

        var ack = AckFactory?.Invoke(command, Commands.Count)
                  ?? NextAck
                  ?? ChatAck.Success(command, [$"stub-{Commands.Count}"], DateTimeOffset.UtcNow);

        return ValueTask.FromResult(ack);
    }
}

internal sealed class TestHost : IAsyncDisposable
{
    private readonly NatterHost _host;

    private TestHost(NatterHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public T Get<T>()
        where T : notnull
        => _host.Services.GetRequiredService<T>();

    public static async Task<TestHost> CreateAsync(Action<NatterApplicationBuilder>? configure = null)
    {
        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .ConfigureOptions(options => options.EnablePluginLoading = false);

        configure?.Invoke(builder);

        var host = builder.Build();
        await host.StartAsync();
        return new TestHost(host);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

internal static class ChatTestData
{
    public static Source Source(string platform = "discord", string selfId = "bot-1", string protocol = "satori")
        => new(protocol, platform, selfId);

    public static Conversation Conversation(string id = "555", ConversationKind kind = ConversationKind.Guild)
        => new(id, kind, GuildId: "444", Title: "general");

    public static User Human(string id = "333", string name = "yg")
        => new(id, name, UserKind.Human, "YG");

    public static Message Message(
        Source source,
        string id = "987",
        Conversation? conversation = null,
        string text = "hello",
        string? replyTo = null)
        => new(
            id,
            conversation ?? Conversation(),
            Human(),
            new Content(text),
            new DateTimeOffset(2026, 11, 14, 14, 0, 0, TimeSpan.Zero),
            replyTo);

    public static MessageCreatedEvent CreatedEvent(
        Source source,
        Message message,
        string eventId = "evt:1",
        long sequence = 1)
        => new()
        {
            Id = eventId,
            Source = source,
            Sequence = sequence,
            OccurredAt = message.CreatedAt,
            ReceivedAt = message.CreatedAt,
            Payload = new MessagePayload(message),
        };
}
