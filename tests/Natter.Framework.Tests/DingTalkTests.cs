using System.Net;
using Microsoft.Extensions.AI;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Chat.DingTalk;
using Natter.Feature.Ai;
using Natter.Feature.Ai.Spark;
using Natter.Infrastructure.Composition;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Natter.Sample.Plugin.DingTalk;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class DingTalkTests
{
    [Fact]
    public void Callback_signature_and_text_message_are_parsed()
    {
        const string secret = "this is a secret";
        const string timestamp = "1577262236757";
        var sign = DingTalkSignature.Sign(timestamp, secret);

        Assert.True(DingTalkSignature.Verify(timestamp, sign, secret, DateTimeOffset.FromUnixTimeMilliseconds(1577262236757), TimeSpan.FromMinutes(1)));
        Assert.False(DingTalkSignature.Verify(timestamp, "wrong", secret, DateTimeOffset.FromUnixTimeMilliseconds(1577262236757), TimeSpan.FromMinutes(1)));
        Assert.False(DingTalkSignature.Verify(timestamp, sign, secret, DateTimeOffset.FromUnixTimeMilliseconds(1577262236757).AddHours(2), TimeSpan.FromHours(1)));

        var parsed = DingTalkCallbackParser.Parse("""
            {
              "senderPlatform": "Mac",
              "conversationId": "cid6EUvB2O8qVF2RYQtHTKEsg==",
              "atUsers": [{ "dingtalkId": "$:LWCP_v1:$PcdFk0c1XakTWLPSQCaVcRzxeEVV3pr5" }],
              "chatbotUserId": "$:LWCP_v1:$PcdFk0c1XakTWLPSQCaVcRzxeEVV3pr5",
              "msgId": "msgrK2ckHkMbaiUWt5xQU+riw==",
              "senderNick": "小钉",
              "senderStaffId": "014728255240768602",
              "sessionWebhookExpiredTime": 1708332604316,
              "createAt": 1708327204136,
              "conversationType": "2",
              "senderId": "$:LWCP_v1:$6GYsn+zrv5WZ77xc2v4zsyXfBv1MhAv9",
              "conversationTitle": "测试群",
              "isInAtList": true,
              "sessionWebhook": "https://oapi.dingtalk.com/robot/sendBySession?session=test",
              "text": { "content": "你好" },
              "msgtype": "text"
            }
            """);

        Assert.True(parsed.Ok);
        Assert.Equal("msgrK2ckHkMbaiUWt5xQU+riw==", parsed.Message!.MsgId);
        Assert.Equal("你好", parsed.Message.Content);
        Assert.Equal("014728255240768602", parsed.Message.EffectiveUserId);
        Assert.True(parsed.Message.IsInAtList);
    }

    [Fact]
    public async Task Upstream_maps_callback_and_sends_through_session_webhook()
    {
        var clock = new TestClock();
        var source = Source.Create("dingtalk", "dingtalk", "bot-dt");
        var options = new DingTalkOptions { BotId = "bot-dt", RequireMention = true, MaxMessageLength = 5 };
        using var transport = new DingTalkFakeTransport();
        using var upstream = new DingTalkChatUpstream(source, transport, options, clock);
        await upstream.ConnectAsync();

        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);
        transport.Emit(Message("m1", "cid", "hello", isAtList: true, atUsers: ["bot-dt"]));

        var created = Assert.Single(events.OfType<MessageCreatedEvent>());
        Assert.Equal(ConversationKind.Group, created.Payload.Message.Conversation.Kind);
        Assert.NotNull(upstream.Health.LastConnectedAt);
        Assert.NotNull(upstream.Health.LastEventAt);
        Assert.Contains(created.Payload.Message.Content.Mentions!, mention => mention.Id == "bot-dt");

        var command = new ChatCommandFactory(clock).Send(
            source,
            created.Payload.Message.Conversation,
            new Content("abcdefghijkl"));
        var ack = await upstream.ExecuteAsync(command);

        Assert.True(ack.Ok);
        Assert.Equal(3, transport.Replies.Count);
        Assert.All(transport.Replies, reply => Assert.Equal("https://oapi.dingtalk.com/robot/sendBySession?session=test", reply.SessionWebhook));
        Assert.Equal(3, ack.Result!.MessageIds!.Count);
        await upstream.DisconnectAsync();
        Assert.Equal(1, upstream.Health.ReconnectCount);
    }

    [Fact]
    public async Task Direct_message_does_not_require_a_mention()
    {
        var clock = new TestClock();
        var source = Source.Create("dingtalk", "dingtalk", "bot-dt");
        using var transport = new DingTalkFakeTransport();
        using var upstream = new DingTalkChatUpstream(
            source,
            transport,
            new DingTalkOptions { BotId = "bot-dt", RequireMention = true },
            clock);
        await upstream.ConnectAsync();
        var events = new List<ChatEvent>();
        using var subscription = upstream.Events.Subscribe(events.Add);

        var direct = Message("direct-1", "dm-1", "hello dm", isAtList: false, atUsers: []) with
        {
            ConversationType = "1",
        };
        transport.Emit(direct);

        var created = Assert.Single(events.OfType<MessageCreatedEvent>());
        Assert.Equal(ConversationKind.Direct, created.Payload.Message.Conversation.Kind);
    }

    [Fact]
    public async Task Unsupported_commands_are_reported_without_transport_calls()
    {
        var clock = new TestClock();
        var source = Source.Create("dingtalk", "dingtalk", "bot-dt");
        using var transport = new DingTalkFakeTransport();
        using var upstream = new DingTalkChatUpstream(
            source,
            transport,
            new DingTalkOptions { BotId = "bot-dt", RequireMention = false },
            clock);
        await upstream.ConnectAsync();

        var update = new ChatCommandFactory(clock).Update(
            source,
            new Conversation("cid", ConversationKind.Group),
            "m1",
            new Content("new"));
        var ack = await upstream.ExecuteAsync(update);

        Assert.False(ack.Ok);
        Assert.Equal(ChatErrorCodes.Unsupported, ack.Error!.Code);
        Assert.Empty(transport.Replies);
    }

    [Fact]
    public async Task DingTalk_plus_Spark_uses_the_same_shared_responder()
    {
        using var temp = new TempDirectory();
        var dingTalkDir = temp.Combine("dingtalk");
        var sparkDir = temp.Combine("spark");
        var configDir = temp.Combine("config");
        Directory.CreateDirectory(dingTalkDir);
        Directory.CreateDirectory(sparkDir);
        Directory.CreateDirectory(configDir);

        File.WriteAllText(
            Path.Combine(dingTalkDir, "config.schema.json"),
            ReadEmbedded(typeof(DingTalkPlugin).Assembly, "Natter.Sample.Plugin.DingTalk.config.schema.json"));
        File.WriteAllText(
            Path.Combine(sparkDir, "config.schema.json"),
            ReadEmbedded(typeof(SparkPlugin).Assembly, "Natter.Feature.Ai.Spark.config.schema.json"));
        File.WriteAllText(Path.Combine(configDir, "natter.transport.messaging.dingtalk.json"), """
            {
              "clientId": "dingtalk-client",
              "clientSecret": "${secret:DINGTALK_CLIENT_SECRET}",
              "botId": "bot-dt",
              "streamGatewayUrl": "https://api.dingtalk.com/v1.0/gateway/connections/open",
              "requireMention": false
            }
            """);
        File.WriteAllText(Path.Combine(configDir, "natter.feature.ai.spark.json"), """
            {
              "apiKey": "${secret:SPARK_API_KEY}",
              "model": "spark-x2.5-1.7b",
              "maxOutputTokens": 128,
              "tools": []
            }
            """);

        using var dingTalk = new DingTalkFakeTransport();
        var spark = new FakeChatClient
        {
            ResponseFactory = _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "spark answer")),
        };
        var secrets = new TestSecrets(new Dictionary<string, string>
        {
            ["DINGTALK_CLIENT_SECRET"] = "dingtalk-secret",
            ["SPARK_API_KEY"] = "spark-key",
        });

        var builder = NatterApplicationBuilder.Create()
            .AddNatterFramework()
            .AddModule<Natter.Cli.Core.CliCoreCompositionModule>()
            .ConfigureOptions(options => options.PluginConfigDirectory = configDir)
            .ConfigurePlugins(options => options.SecretProvider = secrets)
            .AddEmbeddedNpsPlugin(DingTalkPlugin.Manifest, new DingTalkPlugin(dingTalk), dingTalkDir)
            .AddEmbeddedNpsPlugin(SparkPlugin.Manifest, new SparkPlugin(spark), sparkDir);

        await using var host = builder.Build();
        await host.StartAsync();

        var descriptors = host.Services.GetRequiredService<Natter.Abstractions.Plugins.IPluginCatalog>().Plugins;
        Assert.All(descriptors, descriptor => Assert.Equal(Natter.Abstractions.Plugins.PluginState.Activated, descriptor.State));

        var upstream = Assert.Single(host.Services.GetRequiredService<IChatUpstreamRegistry>().Upstreams);
        Assert.Equal("dingtalk:dingtalk:bot-dt", upstream.Source.Key);

        var hostMemory = host.Services.GetRequiredService<Natter.Application.Chat.IMemoryEntryStore>();
        await hostMemory.WriteAsync(new Natter.Application.Chat.MemoryEntryWriteRequest(
            Source.Create("dingtalk", "dingtalk", "bot-dt"),
            new Conversation("cid-cross", ConversationKind.Group, GuildId: "cid-cross"),
            new User("user-1", "User"),
            Natter.Application.Chat.MemoryType.Preference,
            "prefers oolong tea",
            Natter.Application.Chat.MemoryScopeType.Conversation));

        dingTalk.Emit(Message("m-cross", "cid-cross", "hello", isAtList: false, atUsers: []));
        var reply = await WaitForAsync(() => dingTalk.Replies.Count > 0 ? dingTalk.Replies : null);

        Assert.Equal("spark answer", Assert.Single(reply).Content);
        Assert.Single(spark.Calls);
        Assert.Equal("hello", spark.Calls[0][^1].Text);
        Assert.Contains(
            spark.Calls[0],
            message => message.Text!.Contains("oolong tea", StringComparison.Ordinal));
        Assert.DoesNotContain(
            spark.Calls[0],
            message => message.Role == ChatRole.System
                       && message.Text!.Contains("oolong tea", StringComparison.Ordinal));

        var hostStore = host.Services.GetRequiredService<Natter.Application.Chat.IConversationStore>();
        Assert.Equal(1, await hostStore.CountAsync());
        var stored = await hostStore.GetRecentAsync(
            new Natter.Application.Chat.ConversationKey("dingtalk:dingtalk:bot-dt", "cid-cross", "cid-cross", null),
            20,
            24000);
        Assert.Contains(stored, message => message.Text == "hello");

        await host.StopAsync();
    }

    [Fact]
    public async Task Webhook_transport_verifies_signature_and_acknowledges_callbacks()
    {
        var port = FindFreePort();
        var prefix = $"http://127.0.0.1:{port}/dingtalk/";
        var options = new DingTalkOptions { BotId = "bot-dt", ListenPrefix = prefix };
        using var transport = new DingTalkWebhookTransport(options, "secret");
        var events = new List<DingTalkTransportEvent>();
        using var subscription = transport.Events.Subscribe(events.Add);
        await transport.ConnectAsync();

        using var http = new HttpClient();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix)
        {
            Content = new StringContent("""
                {
                  "conversationId": "cid-web",
                  "conversationType": "2",
                  "msgId": "web-1",
                  "senderId": "user-web",
                  "senderNick": "Web User",
                  "createAt": 1708327204136,
                  "text": { "content": "hello webhook" },
                  "msgtype": "text",
                  "isInAtList": true,
                  "atUsers": [{ "dingtalkId": "bot-dt" }]
                }
                """, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("timestamp", timestamp);
        request.Headers.Add("sign", DingTalkSignature.Sign(timestamp, "secret"));

        using var response = await http.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && events.Count == 0)
            await Task.Delay(10);

        var inbound = Assert.Single(events);
        Assert.Equal(DingTalkTransportEventKind.Message, inbound.Kind);
        Assert.Equal("web-1", inbound.Message!.MsgId);

        await transport.DisconnectAsync();
    }

    [Fact]
    public void Stream_protocol_builds_open_request_ack_and_parses_callback_frames()
    {
        var request = JsonDocument.Parse(DingTalkStreamProtocol.BuildOpenConnectionRequest(
            "client-id", "client-secret", DingTalkStreamProtocol.BotTopic));
        Assert.Equal("client-id", request.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("client-secret", request.RootElement.GetProperty("clientSecret").GetString());
        Assert.Equal(
            DingTalkStreamProtocol.BotTopic,
            request.RootElement.GetProperty("subscriptions")[0].GetProperty("topic").GetString());

        var connection = DingTalkStreamProtocol.ParseOpenConnection(
            """{"endpoint":"wss://stream.example/connect","ticket":"a b+c"}""");
        Assert.Equal("wss://stream.example/connect", connection.Endpoint);
        Assert.Contains("ticket=a%20b%2Bc", DingTalkStreamProtocol.BuildWebSocketUri(connection.Endpoint, connection.Ticket));

        var frame = DingTalkStreamProtocol.ParseFrame("""
            {
              "type": "CALLBACK",
              "headers": { "messageId": "msg-1", "topic": "/v1.0/im/bot/messages/get" },
              "data": "{\"msgId\":\"m1\",\"conversationId\":\"cid\"}"
            }
            """);
        Assert.Equal("CALLBACK", frame.Type);
        Assert.Equal("msg-1", frame.MessageId);
        Assert.Equal(DingTalkStreamProtocol.BotTopic, frame.Topic);
        Assert.Contains("m1", frame.Data);

        var ack = JsonDocument.Parse(DingTalkStreamProtocol.BuildAck("msg-1"));
        Assert.Equal(200, ack.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("msg-1", ack.RootElement.GetProperty("headers").GetProperty("messageId").GetString());
    }

    [Fact]
    public async Task Stream_transport_connects_acks_and_publishes_callback()
    {
        var port = FindFreePort();
        using var server = new StreamGatewayServer(port);
        server.Start();

        var options = new DingTalkOptions
        {
            ClientId = "client-id",
            BotId = "bot-dt",
            StreamGatewayUrl = $"http://127.0.0.1:{port}/open",
            ReconnectBaseDelayMs = 10,
            ReconnectMaxDelayMs = 20,
        };
        using var transport = new DingTalkStreamTransport(options, "client-secret");
        var events = new List<DingTalkTransportEvent>();
        using var subscription = transport.Events.Subscribe(events.Add);

        await transport.ConnectAsync();
        var eventTask = WaitForAsync(() => events.FirstOrDefault(item => item.Kind == DingTalkTransportEventKind.Message));
        DingTalkTransportEvent inbound;
        try
        {
            inbound = await eventTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            if (server.AckCode.IsFaulted)
                throw server.AckCode.Exception!;
            throw new TimeoutException(
                "Stream events: " + string.Join(" | ", events.Select(item =>
                    $"{item.Kind}:{item.ErrorCode}:{item.ErrorMessage}:{item.Message?.MsgId}")));
        }
        Assert.Equal("stream-1", inbound.Message!.MsgId);
        Assert.Equal(200, await server.AckCode.WaitAsync(TimeSpan.FromSeconds(5)));
        await transport.DisconnectAsync();
    }

    [Fact]
    public async Task Shared_conversation_store_isolates_sources()
    {
        var options = new AiResponderOptions();
        var store = new AiConversationStore(options);
        var discord = Source.Create("discord", "discord", "d1");
        var dingTalk = Source.Create("dingtalk", "dingtalk", "t1");
        var conversation = new Conversation("same-id", ConversationKind.Direct);

        var discordKey = store.GetKey(discord, conversation, "user");
        var dingTalkKey = store.GetKey(dingTalk, conversation, "user");
        Assert.NotEqual(discordKey, dingTalkKey);

        await store.AppendTurnAsync(discordKey, "discord question", "discord answer");
        Assert.Equal(2, (await store.GetMessagesAsync(discordKey)).Count);
        Assert.Empty(await store.GetMessagesAsync(dingTalkKey));
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(20);
        }
        throw new TimeoutException("Timed out waiting for DingTalk response.");
    }

    private static DingTalkCallbackMessage Message(
        string id,
        string conversationId,
        string text,
        bool isAtList,
        IReadOnlyList<string> atUsers)
        => new(
            id,
            conversationId,
            "2",
            "group",
            "user-1",
            "user-1",
            "User",
            "bot-dt",
            "robot",
            text,
            atUsers,
            isAtList,
            "text",
            "https://oapi.dingtalk.com/robot/sendBySession?session=test",
            DateTimeOffset.UtcNow.AddMinutes(30),
            DateTimeOffset.UtcNow);

    private sealed class StreamGatewayServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<int> _ackCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public StreamGatewayServer(int port)
            => _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        public Task<int> AckCode => _ackCode.Task;

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                _ = HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (context.Request.Url?.AbsolutePath == "/open")
                {
                    var body = System.Text.Encoding.UTF8.GetBytes(
                        """{"endpoint":"ws://127.0.0.1:%PORT%/stream","ticket":"ticket"}"""
                            .Replace("%PORT%", context.Request.Url!.Port.ToString(), StringComparison.Ordinal));
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, _cts.Token);
                    context.Response.Close();
                    return;
                }

                if (context.Request.Url?.AbsolutePath == "/stream")
                {
                    var websocket = await context.AcceptWebSocketAsync(null);
                    var payload = Message(
                        "stream-1",
                        "cid-stream",
                        "hello stream",
                        isAtList: true,
                        atUsers: ["bot-dt"]);
                    var frame = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "CALLBACK",
                        headers = new { messageId = "frame-1", topic = DingTalkStreamProtocol.BotTopic },
                        data = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            msgId = payload.MsgId,
                            conversationId = payload.ConversationId,
                            conversationType = payload.ConversationType,
                            conversationTitle = payload.ConversationTitle,
                            senderId = payload.SenderId,
                            senderStaffId = payload.SenderStaffId,
                            senderNick = payload.SenderNick,
                            chatbotUserId = payload.ChatbotUserId,
                            robotCode = payload.RobotCode,
                            msgtype = "text",
                            text = new { content = payload.Content },
                            atUsers = payload.AtUserIds.Select(id => new { dingtalkId = id }).ToArray(),
                            isInAtList = payload.IsInAtList,
                            sessionWebhook = payload.SessionWebhook,
                            sessionWebhookExpiredTime = payload.SessionWebhookExpiresAt?.ToUnixTimeMilliseconds(),
                            createAt = payload.CreatedAt.ToUnixTimeMilliseconds(),
                        }),
                    });
                    await websocket.WebSocket.SendAsync(
                        System.Text.Encoding.UTF8.GetBytes(frame),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        _cts.Token);

                    var buffer = new byte[8192];
                    var result = await websocket.WebSocket.ReceiveAsync(buffer, _cts.Token);
                    var ack = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count));
                    _ackCode.TrySetResult(ack.RootElement.GetProperty("code").GetInt32());
                    await websocket.WebSocket.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "done",
                        _cts.Token);
                }
            }
            catch (Exception ex)
            {
                _ackCode.TrySetException(ex);
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ReadEmbedded(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class TestSecrets(IReadOnlyDictionary<string, string> values) : ISecretProvider
    {
        public bool TryGet(string name, out string? value)
        {
            var found = values.TryGetValue(name, out var result);
            value = result;
            return found;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "natter-dingtalk-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_root);
        public string Combine(params string[] parts) => Path.Combine([_root, .. parts]);
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
