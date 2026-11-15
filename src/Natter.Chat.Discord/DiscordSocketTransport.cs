using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Discord;
using Discord.Net;
using Discord.WebSocket;

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
    private readonly Subject<DiscordTransportEvent> _events = new();
    private readonly IObservable<DiscordTransportEvent> _synchronizedEvents;
    private readonly ConcurrentDictionary<string, CachedInteraction> _interactions = new(StringComparer.Ordinal);
    private TaskCompletionSource _ready = NewReadySource();
    private bool _started;
    private bool _disposed;
    private int _commandsRegistered;

    public DiscordSocketTransport(DiscordOptions options, DiscordBotToken token)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _token = token ?? throw new ArgumentNullException(nameof(token));

        var config = new DiscordSocketConfig
        {
            GatewayIntents = ParseIntents(options.Intents),
            MessageCacheSize = 100,
            AlwaysDownloadUsers = false,
        };

        _client = new DiscordSocketClient(config);
        _synchronizedEvents = Subject.Synchronize(_events);
        Subscribe();
    }

    public IObservable<DiscordTransportEvent> Events => _synchronizedEvents;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ready = NewReadySource();

        try
        {
            if (!_started)
            {
                await _client.LoginAsync(TokenType.Bot, _token.Value).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);
                _started = true;
            }

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.ReadyTimeoutSeconds)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("Discord gateway did not become ready before the configured timeout.", ex);
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return;

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
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "disconnect_failed",
                ErrorMessage = ex.Message,
            });
        }

        Publish(new DiscordTransportEvent { Kind = DiscordTransportEventKind.Disconnected });
    }

    public async ValueTask<DiscordSendResult> SendMessageAsync(
        DiscordSendMessage request,
        CancellationToken cancellationToken = default)
    {
        try
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
                allowedMentions: BuildAllowedMentions(request.MentionUserIds),
                messageReference: reference,
                options: RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

            return DiscordSendResult.Success(message.Id.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToFailure(ex);
        }
    }

    public async ValueTask<DiscordSendResult> UpdateMessageAsync(
        DiscordUpdateMessage request,
        CancellationToken cancellationToken = default)
    {
        try
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
                    properties.AllowedMentions = BuildAllowedMentions(request.MentionUserIds);
                },
                RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

            return DiscordSendResult.Success(request.MessageId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToFailure(ex);
        }
    }

    public async ValueTask<DiscordSendResult> DeleteMessageAsync(
        DiscordDeleteMessage request,
        CancellationToken cancellationToken = default)
    {
        try
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToFailure(ex);
        }
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
        if (!_interactions.TryGetValue(response.InteractionId, out var cached))
            return DiscordSendResult.Failure("not_found", $"Discord interaction '{response.InteractionId}' is not cached.");

        if (cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _interactions.TryRemove(response.InteractionId, out _);
            return DiscordSendResult.Failure("not_found", "Discord interaction token has expired.");
        }

        try
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
                allowedMentions: AllowedMentions.None,
                options: RequestOptionsFor(cancellationToken)).ConfigureAwait(false);

            return DiscordSendResult.Success(message.Id.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToFailure(ex);
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
        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Ready,
            SelfId = _client.CurrentUser?.Id.ToString(),
            SelfName = _client.CurrentUser?.Username,
            GuildCount = _client.Guilds.Count,
            ChannelCount = _client.Guilds.Sum(guild => guild.Channels.Count),
            LatencyMs = _client.Latency,
        });

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

                var properties = new SlashCommandBuilder()
                    .WithName(name)
                    .WithDescription($"Natter {name} command")
                    .AddOption(
                        "prompt",
                        ApplicationCommandOptionType.String,
                        "Prompt text",
                        isRequired: false)
                    .Build();

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

        _interactions[slash.Id.ToString()] = new CachedInteraction(
            slash,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.InteractionTtlSeconds)));

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
        Publish(new DiscordTransportEvent
        {
            Kind = DiscordTransportEventKind.Disconnected,
            ErrorCode = "gateway_disconnected",
            ErrorMessage = exception.Message,
        });

        return Task.CompletedTask;
    }

    private Task OnLogAsync(LogMessage message)
    {
        if (message.Severity is LogSeverity.Error or LogSeverity.Critical)
        {
            Publish(new DiscordTransportEvent
            {
                Kind = DiscordTransportEventKind.Error,
                ErrorCode = "gateway_log",
                ErrorMessage = message.Exception?.Message ?? message.Message,
            });
        }

        return Task.CompletedTask;
    }

    private void PublishMessage(SocketMessage message, DiscordTransportEventKind kind)
    {
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

    private static AllowedMentions BuildAllowedMentions(IReadOnlyList<string>? mentionUserIds)
    {
        if (mentionUserIds is not { Count: > 0 })
            return AllowedMentions.None;

        var ids = mentionUserIds
            .Select(id => ulong.TryParse(id, out var parsed) ? parsed : (ulong?)null)
            .Where(id => id.HasValue)
            .Select(id => id.GetValueOrDefault())
            .ToList();

        return ids.Count == 0
            ? AllowedMentions.None
            : new AllowedMentions((AllowedMentionTypes?)null)
            {
                UserIds = ids,
                MentionRepliedUser = false,
            };
    }

    private async ValueTask<IMessageChannel?> GetMessageChannelAsync(string channelId)
    {
        if (!ulong.TryParse(channelId, out var id))
            return null;

        await Task.Yield();
        return _client.GetChannel(id) as IMessageChannel;
    }

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

    private void CleanupInteractions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, cached) in _interactions)
        {
            if (cached.ExpiresAt <= now)
                _interactions.TryRemove(id, out _);
        }
    }

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
