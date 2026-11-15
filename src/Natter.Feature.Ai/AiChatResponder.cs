using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Feature.Ai;

/// <summary>
/// NLMP responder backed by <see cref="IChatClient"/>. It owns conversation history,
/// per-conversation serialization, idempotency and user-facing error fallbacks.
/// </summary>
public sealed class AiChatResponder : IChatResponder
{
    private readonly IChatClient _chatClient;
    private readonly AiResponderOptions _options;
    private readonly IAiConversationStore _history;
    private readonly IAiContextAssembler _contextAssembler;
    private readonly AiConversationGate _gate;
    private readonly ChatCommandFactory _commands;
    private readonly IPluginLog _log;
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _completed = new(StringComparer.Ordinal);

    public AiChatResponder(
        IChatClient chatClient,
        AiResponderOptions options,
        IAiConversationStore history,
        AiConversationGate gate,
        ChatCommandFactory commands,
        IPluginLog log,
        IAiContextAssembler? contextAssembler = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _contextAssembler = contextAssembler ?? new RecentWindowContextAssembler(options);
    }

    public async ValueTask<ChatCommand?> RespondAsync(
        ChatEvent chatEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatEvent);

        if (chatEvent is not (MessageCreatedEvent or InteractionCreatedEvent))
            return null;

        if (_completed.ContainsKey(chatEvent.Id) || !_inflight.TryAdd(chatEvent.Id, 0))
            return null;

        try
        {
            var command = chatEvent switch
            {
                MessageCreatedEvent message => await RespondToMessageAsync(message, cancellationToken).ConfigureAwait(false),
                InteractionCreatedEvent interaction => await RespondToInteractionAsync(interaction, cancellationToken).ConfigureAwait(false),
                _ => null,
            };

            if (command is not null)
                _completed.TryAdd(chatEvent.Id, 0);

            return command;
        }
        finally
        {
            _inflight.TryRemove(chatEvent.Id, out _);
            TrimCompleted();
        }
    }

    private async ValueTask<ChatCommand?> RespondToMessageAsync(
        MessageCreatedEvent chatEvent,
        CancellationToken cancellationToken)
    {
        var message = chatEvent.Payload.Message;
        var text = message.Content.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return null;

        var key = _history.GetKey(chatEvent.Source, message.Conversation, message.Sender.Id);
        var generated = await GenerateAsync(
            key,
            chatEvent.Source,
            message.Conversation,
            message.Sender,
            text,
            cancellationToken).ConfigureAwait(false);
        if (generated.Text is null)
            return null;

        var reply = generated.Text;
        await _history.AppendTurnAsync(key, text, reply, chatEvent.Id, cancellationToken).ConfigureAwait(false);
        _log.Information(
            "ai replied conversation={ConversationId} replyChars={ReplyChars}",
            message.Conversation.Id,
            reply.Length);

        return _commands.Send(
            chatEvent.Source,
            message.Conversation,
            new Content(reply),
            replyTo: message.Id,
            idempotencyKey: $"ai:message:{chatEvent.Source.Key}:{message.Id}");
    }

    private async ValueTask<ChatCommand?> RespondToInteractionAsync(
        InteractionCreatedEvent chatEvent,
        CancellationToken cancellationToken)
    {
        var interaction = chatEvent.Payload.Interaction;
        var prompt = ExtractPrompt(interaction);
        if (prompt is null)
        {
            return _commands.Respond(
                chatEvent.Source,
                interaction,
                new Content("Please provide a prompt."),
                ephemeral: true,
                updateOriginal: false);
        }

        var key = _history.GetKey(chatEvent.Source, interaction.Conversation, interaction.User.Id);
        var generated = await GenerateAsync(
            key,
            chatEvent.Source,
            interaction.Conversation,
            interaction.User,
            prompt,
            cancellationToken).ConfigureAwait(false);
        if (generated.Text is null)
            return null;

        var reply = generated.Text;
        await _history.AppendTurnAsync(key, prompt, reply, chatEvent.Id, cancellationToken).ConfigureAwait(false);
        return _commands.Respond(
            chatEvent.Source,
            interaction,
            new Content(reply),
            ephemeral: false,
            updateOriginal: false);
    }

    private async ValueTask<GenerationResult> GenerateAsync(
        string conversationKey,
        Source source,
        Conversation conversation,
        User sender,
        string userText,
        CancellationToken cancellationToken)
    {
        var messages = await _contextAssembler.AssembleAsync(
            source,
            conversation,
            sender,
            userText,
            await _history.GetMessagesAsync(conversationKey, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        try
        {
            using var lease = await _gate.AcquireAsync(conversationKey, cancellationToken).ConfigureAwait(false);
            var profile = Natter.Application.Chat.ProfileContext.Current;
            var options = string.IsNullOrWhiteSpace(profile?.Ai.Model)
                ? null
                : new ChatOptions { ModelId = profile.Ai.Model };
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            var text = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return new GenerationResult(
                    string.IsNullOrWhiteSpace(_options.EmptyResponseText) ? null : _options.EmptyResponseText);

            return new GenerationResult(text);
        }
        catch (Exception ex) when (ex is IAiProviderException provider)
        {
            _log.Warning("ai request failed: kind={Kind} status={Status} code={Code}", provider.Kind, provider.StatusCode, provider.ErrorCode);
            return provider.Kind is AiErrorKind.Timeout
                or AiErrorKind.Unavailable
                or AiErrorKind.ContentFilter
                or AiErrorKind.Unknown
                ? new GenerationResult(FallbackText())
                : new GenerationResult(null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "ai responder failed");
            return new GenerationResult(FallbackText());
        }
    }

    private string? FallbackText()
        => string.IsNullOrWhiteSpace(_options.FallbackText) ? null : _options.FallbackText;

    private static string? ExtractPrompt(Interaction interaction)
    {
        if (interaction.Options is not null
            && interaction.Options.TryGetValue("prompt", out var prompt)
            && prompt.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(prompt.GetString()))
        {
            return prompt.GetString()!.Trim();
        }

        var value = interaction.Values?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        return value?.Trim();
    }

    private sealed record GenerationResult(string? Text);

    private void TrimCompleted()
    {
        if (_completed.Count <= 2048)
            return;

        foreach (var key in _completed.Keys.Take(_completed.Count - 1024))
            _completed.TryRemove(key, out _);
    }
}
