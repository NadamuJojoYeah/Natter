using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Natter.Feature.Ai.DeepSeek;
using Natter.Plugin.Abstractions;

namespace Natter.Framework.Tests;

internal sealed class FakeChatClient : IChatClient
{
    private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = new();
    private readonly ChatClientMetadata _metadata = new("fake", new Uri("https://example.invalid"), "fake-model");

    public Func<IReadOnlyList<ChatMessage>, ChatResponse>? ResponseFactory { get; set; }

    public Func<IReadOnlyList<ChatMessage>, Task<ChatResponse>>? AsyncResponseFactory { get; set; }

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => _calls.ToArray();

    public int Active { get; private set; }

    public int MaxActive { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToArray();
        _calls.Enqueue(list);
        Active++;
        MaxActive = Math.Max(MaxActive, Active);

        try
        {
            if (AsyncResponseFactory is not null)
                return await AsyncResponseFactory(list).ConfigureAwait(false);

            return ResponseFactory?.Invoke(list)
                   ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
        }
        finally
        {
            Active--;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose()
    {
    }
}

internal sealed class FakePluginLog : IPluginLog
{
    public List<string> Entries { get; } = [];

    public void Debug(string message, params object?[] args) => Entries.Add("debug: " + message);

    public void Information(string message, params object?[] args) => Entries.Add("info: " + message);

    public void Warning(string message, params object?[] args) => Entries.Add("warn: " + message);

    public void Error(Exception? exception, string message, params object?[] args)
        => Entries.Add("error: " + message);
}

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, string, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public List<(string Body, HttpRequestMessage Request)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((body, request));
        return responseFactory(request, body);
    }
}

internal static class DeepSeekTestData
{
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
}
