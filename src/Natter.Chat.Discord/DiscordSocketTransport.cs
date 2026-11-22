using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Discord;
using Discord.Net;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Natter.Chat.Discord;

/// <summary>
/// Discord.Net implementation of <see cref="IDiscordTransport"/>. This is the only file that
/// depends on Discord.Net types; upstream mapping stays transport-independent.
/// </summary>
public sealed class DiscordSocketTransport : IDiscordTransport, IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordBotToken _token;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordSocketTransport> _logger;
    private readonly Subject<DiscordTransportEvent> _events = new();
    private readonly IObservable<DiscordTransportEvent> _synchronizedEvents;
    // S2-A11: bounded + TTL. Both grew without bound before (interactions had no capacity at all).
    private readonly DiscordBoundedCache<CachedInteraction> _interactions;
    private readonly DiscordBoundedCache<IMessageChannel> _channels;
    private readonly DiscordSendRetryOptions _retryOptions;
    private const int ChannelCacheCapacity = 500;
    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromMinutes(30);
    private TaskCompletionSource _ready = NewReadySource();
    private bool _started;
    private bool _disposed;
    private int _commandsRegistered;
    private string? _lastGatewayError;
    private int _lastCloseCode = -1;
    private bool _proxyConfigured;
    private bool _restCompleted;
    private bool _gatewayStarted;

    public DiscordSocketTransport(
        DiscordOptions options,
        DiscordBotToken token,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        // Plugins resolve their services from a container that exposes ILoggerFactory but not an
        // open ILogger<> registration, so the factory (not ILogger<T>) is the injectable seam.
        _logger = loggerFactory?.CreateLogger<DiscordSocketTransport>()
            ?? NullLogger<DiscordSocketTransport>.Instance;

        var config = new DiscordSocketConfig
        {
            GatewayIntents = ParseIntents(options.Intents),
            MessageCacheSize = 100,
            AlwaysDownloadUsers = false,
            // Slow egress (proxied links) must not be cut off before the ready window even starts.
            ConnectionTimeout = (int)TimeSpan.FromSeconds(Math.Max(30, options.ReadyTimeoutSeconds)).TotalMilliseconds,
        };

        // Discord.Net defaults ignore the process proxy: REST is created with useProxy: false and
        // the gateway socket gets ClientWebSocketOptions.Proxy = null. Hand both legs the resolved
        // proxy explicitly so REST (including GetBotGateway) and the gateway use the same route.
        if (DiscordEgressProxy.FromEnvironment() is { } egressProxy)
        {
            config.RestClientProvider = DefaultRestClientProvider.Create(useProxy: true, webProxy: egressProxy);
            config.WebSocketProvider = DefaultWebSocketProvider.Create(egressProxy);
            _proxyConfigured = true;
        }

        _client = new DiscordSocketClient(config);

        _interactions = new DiscordBoundedCache<CachedInteraction>(
            Math.Max(1, options.InteractionCacheCapacity),
            TimeSpan.FromSeconds(Math.Max(1, options.InteractionTtlSeconds)));
        _channels = new DiscordBoundedCache<IMessageChannel>(ChannelCacheCapacity, ChannelCacheTtl);
        _retryOptions = new DiscordSendRetryOptions(
            Math.Max(1, options.SendRetryMaxAttempts),
            Math.Max(1, options.SendRetryBaseDelayMs),
            Math.Max(1, options.SendRetryMaxDelayMs));

        _synchronizedEvents = Subject.Synchronize(_events);
        Subscribe();
    }

    public IObservable<DiscordTransportEvent> Events => _synchronizedEvents;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ready = NewReadySource();
        var restarting = _started || _restCompleted || _gatewayStarted;
        _lastGatewayError = null;
        // A close code captured before this connect attempt belongs to the previous socket.
        Volatile.Write(ref _lastCloseCode, -1);
        var startedAt = Stopwatch.StartNew();

        // P1 observability: the 2026-11-22 stall produced zero journal lines for three hours.
        _logger.LogInformation(
            "Discord gateway connect starting (selfId={SelfId}, restart={Restart}, proxy={Proxy}, "
            + "restCompleted={RestCompleted}, gatewayStarted={GatewayStarted}, timeoutSeconds={TimeoutSeconds}).",
            _options.SelfId,
            restarting,
            _proxyConfigured,
            _restCompleted,
            _gatewayStarted,
            _options.ReadyTimeoutSeconds);

        try
        {
            if (!_started)
            {
                await _client.LoginAsync(TokenType.Bot, _token.Value).ConfigureAwait(false);
                _restCompleted = true;
                await _client.StartAsync().ConfigureAwait(false);
                _gatewayStarted = true;
                _started = true;
            }

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.ReadyTimeoutSeconds)), cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Discord gateway connect completed (selfId={SelfId}, connectionState={ConnectionState}, "
                + "latencyMs={LatencyMs}, elapsedMs={ElapsedMs}).",
                _options.SelfId,
                _client.ConnectionState,
                _client.Latency,
                startedAt.ElapsedMilliseconds);
        }
        catch (TimeoutException ex)
        {
            var actualPhase = _lastGatewayError is not null || !_restCompleted
                ? (_restCompleted ? "ws_connect" : "rest_gateway")
                : (_client.ConnectionState == ConnectionState.Connected ? "identify_ready" : "ws_connect");
            _logger.LogError(
                "Discord gateway connect timed out (selfId={SelfId}, phase={Phase}, timeoutSeconds={TimeoutSeconds}, "
                + "connectionState={ConnectionState}, proxy={Proxy}, lastDisconnectedError={LastError}).",
                _options.SelfId,
                actualPhase,
                _options.ReadyTimeoutSeconds,
                _client.ConnectionState,
                _proxyConfigured,
                Redact(_lastGatewayError ?? "none"));
            throw new TimeoutException(
                $"Discord gateway did not become ready during phase '{actualPhase}' within "
                + $"{_options.ReadyTimeoutSeconds}s. lastDisconnectedError="
                + $"{Redact(_lastGatewayError ?? "none")}; connectionState={_client.ConnectionState}; "
                + $"proxy={_proxyConfigured}; restCompleted={_restCompleted}; gatewayStarted={_gatewayStarted}.",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Discord gateway connect cancelled (selfId={SelfId}, elapsedMs={ElapsedMs}).",
                _options.SelfId,
                startedAt.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            var actualPhase = !_restCompleted ? "rest_gateway" : "ws_connect";
            // Never hand the exception object to ILogger: exception text can echo request
            // details, and the journal must stay token-free.
            _logger.LogError(
                "Discord gateway connect failed (selfId={SelfId}, phase={Phase}, errorType={ErrorType}, "
                + "error={Error}, proxy={Proxy}).",
                _options.SelfId,
                actualPhase,
                ex.GetType().Name,
                Redact(ex.Message),
                _proxyConfigured);
            throw new InvalidOperationException(
                $"Discord connect failed during phase '{actualPhase}': {ex.GetType().Name}; "
                + $"lastDisconnectedError={Redact(_lastGatewayError ?? "none")}; proxy={_proxyConfigured}.",
                ex);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

        if (_started)
        {
            _logger.LogInformation(
                "Discord gateway disconnect requested (selfId={SelfId}, connectionState={ConnectionState}).",
                _options.SelfId,
                _client.ConnectionState);
        }

        try
        {
            if (_started)
            {
                await _client.StopAsync().ConfigureAwait(false);
                await _client.LogoutAsync().ConfigureAwait(false);
                _started = false;
            }
        }
        catch (Exception ex)
        {
            // The socket is already unusable when Stop/Logout throws; reset the flags so an
            // external rebuild (the reconnect watchdog) can run Login/Start again instead of
            // waiting on a get-ready handshake that will never happen.
            _started = false;
            _logger.LogWarning(
                "Discord gateway stop failed; marking the transport restartable (selfId={SelfId}, "
                + "errorType={ErrorType}, error={Error}).",
                _options.SelfId,
                ex.GetType().Name,
                Redact(ex.Message));
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "disconnect_failed",
                ErrorMessage = Redact(ex.Message),
            });
        }

        Publish(new DiscordTransportEvent { Kind = DiscordTransportEventKind.Disconnected });
    }

    public async ValueTask<DiscordSendResult> SendMessageAsync(
        DiscordSendMessage request,
        CancellationToken cancellationToken = default)
        => await SendWithRetryAsync(
                $"channel:{request.ChannelId}",
                ct => SendMessageOnceAsync(request, ct),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<DiscordSendResult> SendMessageOnceAsync(
        DiscordSendMessage request,
        CancellationToken cancellationToken)
    {
        var channel = await GetMessageChannelAsync(request.ChannelId).ConfigureAwait(false);
        if (channel is null)
            return DiscordSendResult.Failure("not_found", $"Discord channel '{request.ChannelId}' was not found.");

        var reference = ParseSnowflake(request.ReplyToMessageId) is { } replyId
            ? new MessageReference(
                replyId,
                ulong.TryParse(request.ChannelId, out var referenceChannelId) ? referenceChannelId : null,
                failIfNotExists: false)
            : null;

        var message = await channel.SendMessageAsync(
            request.Content,
            allowedMentions: DiscordMentionPolicy.Build(request.MentionUserIds),
            messageReference: reference,
            options: RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

        return DiscordSendResult.Success(message.Id.ToString());
    }

    public async ValueTask<DiscordSendResult> UpdateMessageAsync(
        DiscordUpdateMessage request,
        CancellationToken cancellationToken = default)
        => await SendWithRetryAsync(
                $"channel:{request.ChannelId}",
                ct => UpdateMessageOnceAsync(request, ct),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<DiscordSendResult> UpdateMessageOnceAsync(
        DiscordUpdateMessage request,
        CancellationToken cancellationToken)
    {
        var channel = await GetMessageChannelAsync(request.ChannelId).ConfigureAwait(false);
        if (channel is null)
            return DiscordSendResult.Failure("not_found", $"Discord channel '{request.ChannelId}' was not found.");

        if (!ulong.TryParse(request.MessageId, out var messageId))
            return DiscordSendResult.Failure("invalid_request", $"Invalid Discord message id '{request.MessageId}'.");

        var message = await channel.GetMessageAsync(messageId, options: RequestOptionsFor(cancellationToken))
            .ConfigureAwait(false);
        if (message is null)
            return DiscordSendResult.Failure("not_found", $"Discord message '{request.MessageId}' was not found.");

        if (message.Author.Id != _client.CurrentUser?.Id)
            return DiscordSendResult.Failure("forbidden", "The bot may only edit its own messages.");

        if (message is not IUserMessage userMessage)
            return DiscordSendResult.Failure("invalid_request", "Discord message is not editable.");

        await userMessage.ModifyAsync(
            properties =>
            {
                properties.Content = request.Content;
                properties.AllowedMentions = DiscordMentionPolicy.Build(request.MentionUserIds);
            },
            RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

        return DiscordSendResult.Success(request.MessageId);
    }

    public async ValueTask<DiscordSendResult> DeleteMessageAsync(
        DiscordDeleteMessage request,
        CancellationToken cancellationToken = default)
        => await SendWithRetryAsync(
                $"channel:{request.ChannelId}",
                ct => DeleteMessageOnceAsync(request, ct),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<DiscordSendResult> DeleteMessageOnceAsync(
        DiscordDeleteMessage request,
        CancellationToken cancellationToken)
    {
        var channel = await GetMessageChannelAsync(request.ChannelId).ConfigureAwait(false);
        if (channel is null)
            return DiscordSendResult.Failure("not_found", $"Discord channel '{request.ChannelId}' was not found.");

        if (!ulong.TryParse(request.MessageId, out var messageId))
            return DiscordSendResult.Failure("invalid_request", $"Invalid Discord message id '{request.MessageId}'.");

        var message = await channel.GetMessageAsync(messageId, options: RequestOptionsFor(cancellationToken))
            .ConfigureAwait(false);
        if (message is null)
            return DiscordSendResult.Failure("not_found", $"Discord message '{request.MessageId}' was not found.");

        if (message.Author.Id != _client.CurrentUser?.Id)
            return DiscordSendResult.Failure("forbidden", "The bot may only delete its own messages.");

        await channel.DeleteMessageAsync(messageId, RequestOptionsFor(cancellationToken))
            .ConfigureAwait(false);
        return DiscordSendResult.Success(request.MessageId);
    }

    public async ValueTask TriggerTypingAsync(
        DiscordConversationRef conversation,
        CancellationToken cancellationToken = default)
    {
        var channel = await GetMessageChannelAsync(conversation.ChannelId).ConfigureAwait(false);
        if (channel is null)
            throw new InvalidOperationException($"Discord channel '{conversation.ChannelId}' was not found.");

        await channel.TriggerTypingAsync(RequestOptionsFor(cancellationToken)).ConfigureAwait(false);
    }

    public async ValueTask<DiscordSendResult> RespondInteractionAsync(
        DiscordInteractionResponse response,
        CancellationToken cancellationToken = default)
    {
        if (!_interactions.TryGet(response.InteractionId, out var cached))
            return DiscordSendResult.Failure("not_found", $"Discord interaction '{response.InteractionId}' is not cached.");

        if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _interactions.Remove(response.InteractionId);
            return DiscordSendResult.Failure("not_found", "Discord interaction token has expired.");
        }

        return await SendWithRetryAsync(
                $"interaction:{response.InteractionId}",
                ct => RespondInteractionOnceAsync(response, cached, ct),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DiscordSendResult> RespondInteractionOnceAsync(
        DiscordInteractionResponse response,
        CachedInteraction cached,
        CancellationToken cancellationToken)
    {
        if (response.UpdateOriginal)
        {
            var modified = await cached.Interaction.ModifyOriginalResponseAsync(
                properties => properties.Content = response.Content,
                RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

            return DiscordSendResult.Success(modified.Id.ToString());
        }

        var message = await cached.Interaction.FollowupAsync(
            response.Content,
            ephemeral: response.Ephemeral,
            allowedMentions: DiscordMentionPolicy.Build(null),
            options: RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

        return DiscordSendResult.Success(message.Id.ToString());
    }

    /// <summary>
    /// S2-A16: bounded retry around a Discord send. Only rate-limit rejections are retried, the
    /// platform's retryAfter wins over our backoff, and exhaustion returns an explicit
    /// <c>rate_limited</c> failure instead of dropping the message silently.
    /// </summary>
    private async Task<DiscordSendResult> SendWithRetryAsync(
        string target,
        Func<CancellationToken, Task<DiscordSendResult>> send,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await send(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (DiscordSendRetryPolicy.IsRateLimited(ex))
            {
                if (!DiscordSendRetryPolicy.ShouldRetry(ex, attempt, _retryOptions))
                {
                    var exhausted = DiscordSendRetryPolicy.Exhausted(ex, attempt, _retryOptions);
                    _logger.LogError(
                        "Discord rate limit persisted; giving up (target={Target}, attempts={Attempts}, "
                        + "retryAfterMs={RetryAfterMs}, errorType={ErrorType}).",
                        target,
                        attempt,
                        DiscordSendRetryPolicy.RetryAfterMs(ex),
                        ex.GetType().Name);
                    return exhausted;
                }

                var delay = DiscordSendRetryPolicy.DelayFor(ex, attempt, _retryOptions);
                _logger.LogWarning(
                    "Discord rate limited; retrying in {DelayMs}ms (target={Target}, attempt={Attempt}/{MaxAttempts}, "
                    + "retryAfterMs={RetryAfterMs}).",
                    (long)delay.TotalMilliseconds,
                    target,
                    attempt,
                    _retryOptions.MaxAttempts,
                    DiscordSendRetryPolicy.RetryAfterMs(ex));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ToFailure(ex);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Unsubscribe();
        _events.OnCompleted();
        _events.Dispose();
        _client.Dispose();
        _interactions.Clear();
        _channels.Clear();
    }

    private void Subscribe()
    {
        _client.Ready += OnReadyAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.MessageUpdated += OnMessageUpdatedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.Disconnected += OnDisconnectedAsync;
        _client.Log += OnLogAsync;
    }

    private void Unsubscribe()
    {
        _client.Ready -= OnReadyAsync;
        _client.MessageReceived -= OnMessageReceivedAsync;
        _client.MessageUpdated -= OnMessageUpdatedAsync;
        _client.MessageDeleted -= OnMessageDeletedAsync;
        _client.InteractionCreated -= OnInteractionCreatedAsync;
        _client.Disconnected -= OnDisconnectedAsync;
        _client.Log -= OnLogAsync;
    }

    private Task OnReadyAsync()
    {
        CleanupInteractions();
        CleanupChannels();
        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Ready,
            SelfId = _client.CurrentUser?.Id.ToString(),
            SelfName = _client.CurrentUser?.Username,
            GuildCount = _client.Guilds.Count,
            ChannelCount = _client.Guilds.Sum(guild => guild.Channels.Count),
            LatencyMs = _client.Latency,
        });

        _logger.LogInformation(
            "Discord gateway ready (selfId={SelfId}, guilds={GuildCount}, channels={ChannelCount}, "
            + "latencyMs={LatencyMs}, connectionState={ConnectionState}).",
            _options.SelfId,
            _client.Guilds.Count,
            _client.Guilds.Sum(guild => guild.Channels.Count),
            _client.Latency,
            _client.ConnectionState);

        _ready.TrySetResult();

        if (Interlocked.Exchange(ref _commandsRegistered, 1) == 0)
            _ = RegisterConfiguredCommandsAsync();

        return Task.CompletedTask;
    }

    private async Task RegisterConfiguredCommandsAsync()
    {
        if (!_options.SlashCommands || _options.Commands.Count == 0)
            return;

        try
        {
            foreach (var (name, enabled) in _options.Commands)
            {
                if (!enabled || string.IsNullOrWhiteSpace(name))
                    continue;

                var builder = new SlashCommandBuilder()
                    .WithName(name)
                    .WithDescription(CommandDescription(name));
                switch (name.ToLowerInvariant())
                {
                    case "ask":
                        builder.AddOption("prompt", ApplicationCommandOptionType.String, "Prompt text", isRequired: true);
                        break;
                    case "remember":
                        builder.AddOption("text", ApplicationCommandOptionType.String, "Memory text", isRequired: true);
                        builder.AddOption(new SlashCommandOptionBuilder()
                            .WithName("scope")
                            .WithDescription("Memory scope")
                            .WithType(ApplicationCommandOptionType.String)
                            .AddChoice("user", "user")
                            .AddChoice("conversation", "conversation")
                            .AddChoice("guild", "guild"));
                        break;
                    case "forget":
                        builder.AddOption("target", ApplicationCommandOptionType.String, "Memory id or text", isRequired: true);
                        break;
                    case "memory":
                        builder.AddOption("limit", ApplicationCommandOptionType.Integer, "Maximum items", isRequired: false);
                        break;
                    case "task":
                        builder.AddOption("prompt", ApplicationCommandOptionType.String, "Task prompt", isRequired: true);
                        break;
                    case "status":
                        builder.AddOption("task", ApplicationCommandOptionType.String, "Task id", isRequired: true);
                        break;
                    case "tasks":
                        break;
                    case "cancel":
                        builder.AddOption("task", ApplicationCommandOptionType.String, "Task id", isRequired: true);
                        break;
                    case "reset":
                        break;
                    default:
                        builder.AddOption("prompt", ApplicationCommandOptionType.String, "Prompt text", isRequired: false);
                        break;
                }
                var properties = builder.Build();

                if (ulong.TryParse(_options.DevGuildId, out var guildId)
                    && _client.GetGuild(guildId) is { } guild)
                {
                    await guild.CreateApplicationCommandAsync(properties).ConfigureAwait(false);
                }
                else
                {
                    await _client.CreateGlobalApplicationCommandAsync(properties).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _commandsRegistered, 0);
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "command_registration_failed",
                ErrorMessage = ex.Message,
                Fatal = false,
            });
        }
    }

    private static string CommandDescription(string name)
        => name.ToLowerInvariant() switch
        {
            "ask" => "Ask Natter",
            "remember" => "Remember an explicit user preference",
            "forget" => "Forget a memory",
            "memory" => "List your visible memories",
            "reset" => "Reset the current conversation",
            _ => $"Natter {name} command",
        };

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        PublishMessage(message, DiscordTransportEventKind.MessageCreated);
        return Task.CompletedTask;
    }

    private Task OnMessageUpdatedAsync(Cacheable<IMessage, ulong> previous, SocketMessage message, ISocketMessageChannel channel)
    {
        PublishMessage(message, DiscordTransportEventKind.MessageUpdated);
        return Task.CompletedTask;
    }

    private Task OnMessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        var id = message.HasValue ? message.Value.Id : message.Id;
        var resolved = channel.HasValue ? channel.Value : _client.GetChannel(channel.Id) as IMessageChannel;

        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.MessageDeleted,
            MessageId = id.ToString(),
            Channel = resolved is ISocketMessageChannel socketChannel ? MapChannel(socketChannel) : null,
        });

        return Task.CompletedTask;
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        if (!_options.SlashCommands)
            return;

        if (interaction is not SocketSlashCommand slash)
        {
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "unsupported_interaction",
                ErrorMessage = $"Discord interaction '{interaction.Type}' is not supported by this transport.",
                Fatal = false,
            });
            return;
        }

        try
        {
            await slash.DeferAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "interaction_defer_failed",
                ErrorMessage = ex.Message,
                Fatal = false,
            });
            return;
        }

        _interactions.Set(
            slash.Id.ToString(),
            new CachedInteraction(slash, DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.InteractionTtlSeconds))));
        if (slash.Channel is IMessageChannel interactionChannel)
            CacheChannel(interactionChannel);

        var options = slash.Data.Options
            .GroupBy(option => option.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ToJson(group.Last().Value),
                StringComparer.Ordinal);

        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.InteractionCreated,
            Interaction = new DiscordInteraction(
                slash.Id.ToString(),
                "slash",
                MapUser(slash.User),
                MapChannel(slash.Channel),
                slash.Channel is SocketGuildChannel guildChannel
                    ? new DiscordGuild(guildChannel.Guild.Id.ToString(), guildChannel.Guild.Name)
                    : null,
                slash.CommandName,
                options,
                null,
                slash.Token,
                slash.CreatedAt),
        });
    }

    private Task OnDisconnectedAsync(Exception exception)
    {
        // Discord.Net wraps every reconnectable close in GatewayReconnectException with the fixed
        // message "Server requested a reconnect". Recover the numeric close code that was captured
        // from the gateway log/exception before it is flattened into that message.
        var reason = Redact(exception?.Message) is { Length: > 0 } text
            ? text
            : "gateway disconnected without exception detail";
        // Written from Discord.Net's log/websocket threads; read once and clear atomically so a
        // stale code can never be attributed to the next disconnect.
        var cachedCloseCode = Volatile.Read(ref _lastCloseCode);
        Volatile.Write(ref _lastCloseCode, -1);
        var closeCode = cachedCloseCode >= 0
            ? cachedCloseCode
            : DiscordGatewayCloseCode.TryExtract(exception);
        var errorCode = DiscordGatewayCloseCode.ToDisconnectCode(closeCode);
        var detail = DiscordGatewayCloseCode.ToDisconnectMessage(closeCode, reason);

        _lastGatewayError = detail;

        _logger.LogWarning(
            "Discord gateway disconnected (selfId={SelfId}, code={Code}, closeCode={CloseCode}, "
            + "reason={Reason}, connectionState={ConnectionState}).",
            _options.SelfId,
            errorCode,
            closeCode?.ToString() ?? "unknown",
            reason,
            _client.ConnectionState);

        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = errorCode,
            ErrorMessage = detail,
        });

        return Task.CompletedTask;
    }

    private Task OnLogAsync(LogMessage message)
    {
        // Discord.Net logs "The server sent close <code> (<reason>)" at warning/error level. Scan
        // every severity: the close code is our only source of a real disconnect cause.
        var raw = DescribeGatewayLog(message);
        if (DiscordGatewayCloseCode.TryExtract(raw) is { } closeCode)
            Volatile.Write(ref _lastCloseCode, closeCode);

        if (message.Severity is LogSeverity.Error or LogSeverity.Critical)
        {
            _lastGatewayError = Redact(message.Exception?.Message ?? message.Message);
            var captured = Volatile.Read(ref _lastCloseCode);
            _logger.LogWarning(
                "Discord.Net reported an error (source={Source}, severity={Severity}, closeCode={CloseCode}, message={Message}).",
                message.Source,
                message.Severity,
                captured >= 0 ? captured.ToString() : "unknown",
                Redact(message.Message));
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "gateway_log",
                ErrorMessage = _lastGatewayError,
            });
        }
        else
        {
            _logger.LogDebug(
                "Discord.Net log (source={Source}, severity={Severity}, message={Message}).",
                message.Source,
                message.Severity,
                Redact(message.Message));
        }

        return Task.CompletedTask;
    }

    private static string DescribeGatewayLog(LogMessage message)
    {
        var text = message.Message ?? string.Empty;
        if (message.Exception is null)
            return text;

        return string.IsNullOrWhiteSpace(message.Exception.Message)
            ? text
            : $"{text} {message.Exception.Message}";
    }

    private string Redact(string? value)
        => DiscordLogRedaction.Redact(value, _token.Value);

    private void PublishMessage(SocketMessage message, DiscordTransportEventKind kind)
    {
        CacheChannel(message.Channel);

        Publish(new DiscordTransportEvent
        {
            Kind = kind,
            Message = MapMessage(message),
        });
    }

    private DiscordMessage MapMessage(SocketMessage message)
        => new(
            message.Id.ToString(),
            MapChannel(message.Channel),
            message.Channel is SocketGuildChannel guildChannel
                ? new DiscordGuild(guildChannel.Guild.Id.ToString(), guildChannel.Guild.Name)
                : null,
            MapUser(message.Author),
            message.Content ?? string.Empty,
            message.CreatedAt,
            message.EditedTimestamp,
            message.Reference?.MessageId is { } reference ? reference.ToString() : null,
            message.Attachments.Select(MapAttachment).ToArray(),
            message.MentionedUsers.Select(MapUser).ToArray(),
            message.MentionedEveryone);

    private static DiscordUser MapUser(IUser user)
        => new(user.Id.ToString(), user.Username, user.GlobalName, user.IsBot);

    private static DiscordAttachment MapAttachment(IAttachment attachment)
        => new(
            attachment.Id.ToString(),
            attachment.Url,
            attachment.Filename,
            attachment.ContentType,
            attachment.Size,
            attachment.Width,
            attachment.Height);

    private static DiscordChannel MapChannel(ISocketMessageChannel channel)
    {
        if (channel is SocketDMChannel dm)
            return new DiscordChannel(dm.Id.ToString(), IsDirect: true, Name: dm.Recipient?.Username);

        if (channel is SocketGroupChannel group)
            return new DiscordChannel(group.Id.ToString(), IsGroup: true, Name: group.Name);

        if (channel is SocketThreadChannel thread)
            return new DiscordChannel(
                thread.Id.ToString(),
                thread.Guild.Id.ToString(),
                thread.Name,
                thread.GetChannelType() is { } threadType ? (int)threadType : null,
                thread.ParentChannel.Id.ToString(),
                IsThread: true);

        if (channel is SocketGuildChannel guildChannel)
            return new DiscordChannel(
                guildChannel.Id.ToString(),
                guildChannel.Guild.Id.ToString(),
                guildChannel.Name,
                guildChannel.GetChannelType() is { } guildType ? (int)guildType : null);

        return new DiscordChannel(channel.Id.ToString());
    }

    private static JsonElement ToJson(object? value)
    {
        if (value is null)
            return JsonSerializer.SerializeToElement<object?>(null);

        return value switch
        {
            IUser user => JsonSerializer.SerializeToElement(user.Id.ToString()),
            IChannel channel => JsonSerializer.SerializeToElement(channel.Id.ToString()),
            _ => JsonSerializer.SerializeToElement(value),
        };
    }

    private ValueTask<IMessageChannel?> GetMessageChannelAsync(string channelId)
    {
        if (!ulong.TryParse(channelId, out var id))
            return ValueTask.FromResult<IMessageChannel?>(null);

        if (_channels.TryGet(channelId, out var cached))
            return ValueTask.FromResult<IMessageChannel?>(cached);

        var channel = _client.GetChannel(id) as IMessageChannel;
        if (channel is not null)
            CacheChannel(channel);
        return ValueTask.FromResult(channel);
    }

    private void CacheChannel(IMessageChannel channel)
        => _channels.Set(channel.Id.ToString(), channel);

    private void CleanupChannels() => _channels.Prune();

    private static ulong? ParseSnowflake(string? value)
        => ulong.TryParse(value, out var id) ? id : null;

    private static RequestOptions RequestOptionsFor(CancellationToken cancellationToken)
        => new() { CancelToken = cancellationToken };

    private static DiscordSendResult ToFailure(Exception exception)
    {
        return exception switch
        {
            RateLimitedException
                => DiscordSendResult.Failure("rate_limited", "Discord rate limit."),
            HttpException http when http.HttpCode == HttpStatusCode.TooManyRequests
                => DiscordSendResult.Failure("rate_limited", http.Reason ?? "Discord rate limit."),
            HttpException http when http.HttpCode == HttpStatusCode.Forbidden
                => DiscordSendResult.Failure("forbidden", http.Reason ?? "Discord permission denied."),
            HttpException http when http.HttpCode == HttpStatusCode.NotFound
                => DiscordSendResult.Failure("not_found", http.Reason ?? "Discord resource not found."),
            HttpException http
                => DiscordSendResult.Failure("internal", $"Discord HTTP {(int)http.HttpCode}: {http.Reason}"),
            TimeoutException
                => DiscordSendResult.Failure("timeout", "Discord request timed out."),
            _ => DiscordSendResult.Failure("internal", exception.Message),
        };
    }

    private void CleanupInteractions() => _interactions.Prune();

    private void Publish(DiscordTransportEvent transportEvent)
        => _events.OnNext(transportEvent);

    private static TaskCompletionSource NewReadySource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GatewayIntents ParseIntents(IEnumerable<string> values)
    {
        var intents = GatewayIntents.None;

        foreach (var value in values)
        {
            if (!Enum.TryParse<GatewayIntents>(value, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unknown Discord gateway intent '{value}'.", nameof(values));

            intents |= parsed;
        }

        return intents;
    }

    private sealed record CachedInteraction(SocketInteraction Interaction, DateTimeOffset ExpiresAt);
}
