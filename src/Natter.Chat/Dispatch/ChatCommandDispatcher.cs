using System.Text.Json;
using Natter.Abstractions.Ports;
using Natter.Chat.Safety;

namespace Natter.Chat;

/// <summary>
/// Resolves the upstream for a command, validates content and mentions, applies capability
/// degradation, chunks long text and reuses idempotency keys. Adapters still perform their
/// own wire-level validation and deduplication.
/// </summary>
public sealed class ChatCommandDispatcher : IChatCommandDispatcher
{
    private readonly IChatUpstreamRegistry _upstreams;
    private readonly ChatIdempotencyCache _idempotency;
    private readonly ChatOutboundPolicy _policy;
    private readonly IClock _clock;
    private readonly ChatChunkProgress _chunkProgress;

    public ChatCommandDispatcher(
        IChatUpstreamRegistry upstreams,
        ChatIdempotencyCache idempotency,
        IClock clock,
        ChatOutboundPolicy? policy = null)
    {
        _upstreams = upstreams ?? throw new ArgumentNullException(nameof(upstreams));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? ChatOutboundPolicy.Default;
        _chunkProgress = new ChatChunkProgress(clock: () => _clock.UtcNow);
    }

    public async ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Id))
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                "Command id must not be empty."));
        }

        var sourceError = ValidateSource(command.Source);
        if (sourceError is not null)
            return ChatAck.Failure(command, sourceError);

        if (!_upstreams.TryGet(command.Source, out var upstream) || upstream is null)
        {
            return ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Unavailable,
                $"No upstream is registered for source '{command.Source.Key}'."));
        }

        var key = command.IdempotencyKey;
        var reserved = false;
        if (!string.IsNullOrWhiteSpace(key))
        {
            // S2-A1: reserve before executing so two concurrent calls with the same key cannot
            // both reach the adapter. A failed execution rolls the reservation back for retry.
            switch (_idempotency.TryReserve(key, out var cached))
            {
                case ChatIdempotencyCache.Reservation.Cached:
                    return cached!;
                case ChatIdempotencyCache.Reservation.InFlight:
                    return ChatAck.Failure(command, ChatError.Create(
                        ChatErrorCodes.Conflict,
                        $"A command with idempotency key '{key}' is already in flight."));
                default:
                    reserved = true;
                    break;
            }
        }

        ChatAck ack;
        try
        {
            ack = await ExecuteCoreAsync(upstream, command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (reserved)
                _idempotency.Rollback(key);
            throw;
        }
        catch (Exception exception)
        {
            ack = ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Internal,
                $"The upstream failed while executing '{command.Type}': {exception.Message}"));
        }

        if (reserved)
        {
            if (ack.Ok)
                _idempotency.Commit(key, ack);
            else
                _idempotency.Rollback(key);
        }

        return ack;
    }

    private async ValueTask<ChatAck> ExecuteCoreAsync(
        IChatUpstream upstream,
        ChatCommand command,
        CancellationToken cancellationToken)
    {
        return command switch
        {
            MessageSendCommand send => await ExecuteSendAsync(upstream, send, cancellationToken).ConfigureAwait(false),
            MessageUpdateCommand update => await ExecuteUpdateAsync(upstream, update, cancellationToken).ConfigureAwait(false),
            MessageDeleteCommand delete => await ExecuteDeleteAsync(upstream, delete, cancellationToken).ConfigureAwait(false),
            TypingStartCommand typing => await ExecuteTypingAsync(upstream, typing, cancellationToken).ConfigureAwait(false),
            InteractionRespondCommand respond => await ExecuteRespondAsync(upstream, respond, cancellationToken).ConfigureAwait(false),
            _ => ChatAck.Failure(command, ChatError.Create(
                ChatErrorCodes.Unsupported,
                $"Command type '{command.Type}' is not supported by NLMP v0.1.")),
        };
    }

    private async ValueTask<ChatAck> ExecuteSendAsync(
        IChatUpstream upstream,
        MessageSendCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        if (payload.Conversation is null || string.IsNullOrWhiteSpace(payload.Conversation.Id))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send requires conversation.id.");

        if (payload.Content is null)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send requires content.");

        var contentErrors = payload.Content.Validate();
        if (contentErrors.Count > 0)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.send content is invalid.", contentErrors);

        var mentionError = ChatMentionGuard.Validate(payload.Content, _policy);
        if (mentionError is not null)
            return ChatAck.Failure(command, mentionError);

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Fail(
                command,
                ChatErrorCodes.InvalidRequest,
                "message.send MUST provide an idempotencyKey (retries must reuse it).");
        }

        var capabilities = await upstream.GetCapabilitiesAsync(payload.Conversation, cancellationToken)
            .ConfigureAwait(false);

        var degraded = ChatCapabilityDegrader.DegradeSend(command, capabilities);
        if (!capabilities.SupportsConversation(payload.Conversation.Kind)
            && capabilities.ConversationKinds.Count > 0)
        {
            return Fail(
                command,
                ChatErrorCodes.Unsupported,
                $"Conversation kind '{payload.Conversation.Kind}' is not supported by source '{command.Source.Key}'.");
        }

        var chunks = ChatTextChunker.Chunk(degraded.Payload.Content, capabilities.MaxMessageLength);
        // S2-A15: resume from the first undelivered chunk on retry; the platform already accepted
        // the earlier chunks, so replaying them would duplicate the message.
        var progressKey = degraded.IdempotencyKey;
        var progress = _chunkProgress.Get(progressKey);
        var startIndex = progress is { } saved && saved.NextIndex < chunks.Count ? saved.NextIndex : 0;
        var messageIds = progress is { } existing ? existing.MessageIds.ToList() : new List<string>(chunks.Count);
        DateTimeOffset? deliveredAt = null;

        for (var index = startIndex; index < chunks.Count; index++)
        {
            var chunkContent = chunks[index];
            var chunkCommand = degraded with
            {
                Id = index == 0 ? degraded.Id : $"{degraded.Id}:chunk-{index}",
                IdempotencyKey = index == 0
                    ? degraded.IdempotencyKey
                    : $"{degraded.IdempotencyKey}:chunk-{index}",
                Payload = degraded.Payload with
                {
                    Content = chunkContent,
                    ReplyTo = index == 0 ? degraded.Payload.ReplyTo : null,
                },
            };

            var chunkAck = await upstream.ExecuteAsync(chunkCommand, cancellationToken).ConfigureAwait(false);
            if (!chunkAck.Ok)
            {
                var error = chunkAck.Error ?? ChatError.Create(ChatErrorCodes.Internal, "The upstream returned a failed ack without an error.");
                return ChatAck.Failure(command, WithDeliveredIds(error, messageIds));
            }

            if (chunkAck.Result?.MessageIds is { Count: > 0 } ids)
            {
                messageIds.AddRange(ids);
                _chunkProgress.Record(progressKey, index + 1, ids);
            }
            else
            {
                _chunkProgress.Record(progressKey, index + 1, []);
            }

            deliveredAt ??= chunkAck.Result?.DeliveredAt;
        }

        _chunkProgress.Complete(progressKey);
        return ChatAck.Success(command, messageIds, deliveredAt ?? _clock.UtcNow);
    }

    private async ValueTask<ChatAck> ExecuteUpdateAsync(
        IChatUpstream upstream,
        MessageUpdateCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        if (payload.Conversation is null || string.IsNullOrWhiteSpace(payload.MessageId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update requires conversation.id and messageId.");

        if (payload.Content is null)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update requires content.");

        var contentErrors = payload.Content.Validate();
        if (contentErrors.Count > 0)
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.update content is invalid.", contentErrors);

        var mentionError = ChatMentionGuard.Validate(payload.Content, _policy);
        if (mentionError is not null)
            return ChatAck.Failure(command, mentionError);

        var capabilities = await upstream.GetCapabilitiesAsync(payload.Conversation, cancellationToken)
            .ConfigureAwait(false);

        if (!capabilities.SupportsEdit)
        {
            return Fail(
                command,
                ChatErrorCodes.Unsupported,
                $"Message editing is not supported by source '{command.Source.Key}'; send the final message only.");
        }

        var degraded = ChatCapabilityDegrader.DegradeUpdate(command, capabilities);
        return await upstream.ExecuteAsync(degraded, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ChatAck> ExecuteDeleteAsync(
        IChatUpstream upstream,
        MessageDeleteCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        if (payload.Conversation is null || string.IsNullOrWhiteSpace(payload.MessageId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "message.delete requires conversation.id and messageId.");

        var capabilities = await upstream.GetCapabilitiesAsync(payload.Conversation, cancellationToken)
            .ConfigureAwait(false);

        if (!capabilities.Delete)
        {
            return Fail(
                command,
                ChatErrorCodes.Unsupported,
                $"Message deletion is not supported by source '{command.Source.Key}'; intermediate messages are kept.");
        }

        return await upstream.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ChatAck> ExecuteTypingAsync(
        IChatUpstream upstream,
        TypingStartCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Payload.Conversation is null || string.IsNullOrWhiteSpace(command.Payload.Conversation.Id))
            return Fail(command, ChatErrorCodes.InvalidRequest, "typing.start requires conversation.id.");

        var capabilities = await upstream.GetCapabilitiesAsync(command.Payload.Conversation, cancellationToken)
            .ConfigureAwait(false);

        // Degradation: typing=false means skip it, not fail the reply pipeline.
        if (!capabilities.Typing)
            return ChatAck.Success(command, deliveredAt: _clock.UtcNow);

        return await upstream.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ChatAck> ExecuteRespondAsync(
        IChatUpstream upstream,
        InteractionRespondCommand command,
        CancellationToken cancellationToken)
    {
        var payload = command.Payload;
        if (string.IsNullOrWhiteSpace(payload.InteractionId))
            return Fail(command, ChatErrorCodes.InvalidRequest, "interaction.respond requires interactionId.");

        if (payload.Content is null)
            return Fail(command, ChatErrorCodes.InvalidRequest, "interaction.respond requires content.");

        var contentErrors = payload.Content.Validate();
        if (contentErrors.Count > 0)
            return Fail(command, ChatErrorCodes.InvalidRequest, "interaction.respond content is invalid.", contentErrors);

        var mentionError = ChatMentionGuard.Validate(payload.Content, _policy);
        if (mentionError is not null)
            return ChatAck.Failure(command, mentionError);

        var capabilities = await upstream.GetCapabilitiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!capabilities.Buttons && !capabilities.Modals && !capabilities.SlashCommands)
        {
            return Fail(
                command,
                ChatErrorCodes.Unsupported,
                $"Interactive responses are not supported by source '{command.Source.Key}'.");
        }

        var degraded = ChatCapabilityDegrader.DegradeRespond(command, capabilities);
        return await upstream.ExecuteAsync(degraded, cancellationToken).ConfigureAwait(false);
    }

    private static ChatError? ValidateSource(Source? source)
    {
        if (source is null)
            return ChatError.Create(ChatErrorCodes.InvalidRequest, "Command source must not be null.");

        if (string.IsNullOrWhiteSpace(source.Protocol)
            || string.IsNullOrWhiteSpace(source.Platform)
            || string.IsNullOrWhiteSpace(source.SelfId))
        {
            return ChatError.Create(
                ChatErrorCodes.InvalidRequest,
                "Command source requires non-empty protocol, platform and selfId.");
        }

        return null;
    }

    private static ChatAck Fail(ChatCommand command, string code, string message, IReadOnlyList<string>? errors = null)
    {
        IReadOnlyDictionary<string, JsonElement>? details = null;
        if (errors is { Count: > 0 })
        {
            details = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["errors"] = JsonSerializer.SerializeToElement(errors, NlmpJson.Options),
            };
        }

        return ChatAck.Failure(command, ChatError.Create(code, message, details: details));
    }

    private static ChatError WithDeliveredIds(ChatError error, IReadOnlyList<string> deliveredIds)
    {
        if (deliveredIds.Count == 0)
            return error;

        var details = error.Details is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(error.Details, StringComparer.Ordinal);

        details["deliveredMessageIds"] = JsonSerializer.SerializeToElement(deliveredIds, NlmpJson.Options);
        return error with { Details = details };
    }
}
