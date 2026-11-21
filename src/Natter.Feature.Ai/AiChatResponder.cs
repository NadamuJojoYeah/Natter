using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Plugin.Abstractions;

namespace Natter.Feature.Ai;

/// <summary>
/// NLMP responder backed by <see cref="IChatClient"/>. It owns conversation history,
/// per-conversation serialization, idempotency and user-facing error fallbacks.
/// </summary>
public sealed partial class AiChatResponder : IChatResponder
{
    private readonly IChatClient _chatClient;
    private readonly AiResponderOptions _options;
    private readonly IAiConversationStore _history;
    private readonly IAiContextAssembler _contextAssembler;
    private readonly Natter.Application.Chat.IContextPipeline? _contextPipeline;
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
        IAiContextAssembler? contextAssembler = null,
        Natter.Application.Chat.IContextPipeline? contextPipeline = null,
        Natter.Application.Chat.ToolProtocolCapabilities? toolProtocols = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _contextAssembler = contextAssembler ?? new RecentWindowContextAssembler(options);
        _contextPipeline = contextPipeline;
        ProtocolCapabilities = toolProtocols ?? Natter.Application.Chat.ToolProtocolCapabilities.TextOnly;
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
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages;
        if (_contextPipeline is not null)
        {
            try
            {
                var variables = new Dictionary<string, string>
                {
                    ["systemPrompt"] = _options.SystemPrompt,
                };
                var bundle = await _contextPipeline.AssembleAsync(
                    source,
                    conversation,
                    sender,
                    userText,
                    variables,
                    cancellationToken).ConfigureAwait(false);
                messages = bundle.Messages;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning("context pipeline failed: {Type}; falling back to recent window", ex.GetType().Name);
                messages = await _contextAssembler.AssembleAsync(
                    source,
                    conversation,
                    sender,
                    userText,
                    await _history.GetMessagesAsync(conversationKey, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            messages = await _contextAssembler.AssembleAsync(
                source,
                conversation,
                sender,
                userText,
                await _history.GetMessagesAsync(conversationKey, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var lease = await _gate.AcquireAsync(conversationKey, cancellationToken).ConfigureAwait(false);
            var profile = Natter.Application.Chat.ProfileContext.Current;
            var options = string.IsNullOrWhiteSpace(profile?.Ai.Model)
                ? null
                : new ChatOptions { ModelId = profile.Ai.Model };
            var text = await CallAsync(messages, options, cancellationToken).ConfigureAwait(false);
            if (IsClipped(text))
            {
                _log.Warning("AI provider returned a clipped response; retrying once.");
                text = await CallAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }

            if (IsClipped(text))
            {
                _log.Warning("AI provider still returned a clipped response; using fallback text.");
                return new GenerationResult(FallbackText());
            }

            // Provider wrapper tags are an output-boundary artifact; strip them only here, after
            // clipped detection, so the clipped retry/fallback path keeps seeing the raw markers.
            var cleaned = StripResponseEnvelope(text);
            if (string.IsNullOrWhiteSpace(cleaned))
                return new GenerationResult(
                    string.IsNullOrWhiteSpace(_options.EmptyResponseText) ? null : _options.EmptyResponseText);

            return new GenerationResult(cleaned);
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

    private async ValueTask<string?> CallAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false);
        return response.Text?.Trim();
    }

    private static readonly Regex LeadingResponseTag = new(
        @"^\s*</?response\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex TrailingResponseTag = new(
        @"</?response\s*>\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Strips provider wrapper tags (<c>&lt;response&gt;</c> / <c>&lt;/response&gt;</c>) from the
    /// start and end of the model output only. Tags inside the body (code fences, inline HTML,
    /// prose) are left untouched, and repeated/nested wrappers are unwrapped.
    /// </summary>
    internal static string StripResponseEnvelope(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var value = text;
        for (var guard = 0; guard < 4; guard++)
        {
            var before = value;
            value = LeadingResponseTag.Replace(value, string.Empty, 1);
            value = TrailingResponseTag.Replace(value, string.Empty, 1);
            if (string.Equals(before, value, StringComparison.Ordinal))
                break;
        }

        return value.Trim();
    }

    private static bool IsClipped(string? text)
        => text is not null
           && (text.Contains("<response clipped>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<response truncated>", StringComparison.OrdinalIgnoreCase)
               || text.Contains("[response clipped]", StringComparison.OrdinalIgnoreCase));

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
