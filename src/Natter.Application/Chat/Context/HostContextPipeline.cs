using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Application.Chat;

/// <summary>
/// Host-owned prompt → summary/memory → context pipeline. Profile policy is read from
/// ProfileContext (set by ProfileAwareChatResponder); every dependency is optional in the
/// sense that a failure degrades to recent-window context instead of blocking the reply.
/// </summary>
public sealed class HostContextPipeline(
    IConversationStore conversations,
    IMemoryStore memories,
    ISummaryStore summaries,
    IPromptProvider prompts,
    IContextAssembler assembler,
    IContextComponentCatalog? components = null) : IContextPipeline
{
    private static ConversationSummary BuildDeterministicSummary(
        ConversationKey key,
        IReadOnlyList<StoredMessage> older,
        string promptVersion,
        ConversationSummary? previous)
    {
        var first = older.FirstOrDefault();
        var last = older.LastOrDefault();
        var samples = older
            .Where((_, index) => index < 3 || index >= older.Count - 3)
            .Select(message => $"{message.Role}: {Truncate(message.Text, 180)}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var previousText = previous is null ? string.Empty : Truncate(previous.Text, 1200) + "\n";
        return new ConversationSummary(
            "sum_" + Guid.NewGuid().ToString("N")[..12],
            key,
            first?.MessageId ?? 0,
            last?.MessageId ?? 0,
            Truncate(previousText + "Earlier conversation:\n" + string.Join("\n", samples), 2000),
            (previous?.Version ?? 0) + 1,
            promptVersion,
            DateTimeOffset.UtcNow);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static readonly RuntimeProfile FallbackProfile = new()
    {
        Id = "host-default",
        Ai = new ProfileAi { Provider = "host#default" },
    };

    public async ValueTask<ContextBundle> AssembleAsync(
        Source source,
        Conversation conversation,
        User user,
        string userText,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        var profile = ProfileContext.Current ?? FallbackProfile;
        var promptProvider = components is not null
                             && components.TryGetPrompt(profile.Prompt.Provider, out var selectedPrompt)
                             && selectedPrompt is not null
            ? selectedPrompt
            : prompts;
        var contextAssembler = components is not null
                               && components.TryGetAssembler(profile.Context.Assembler, out var selectedAssembler)
                               && selectedAssembler is not null
            ? selectedAssembler
            : assembler;
        var memoryStore = components is not null
                          && components.TryGetMemory(profile.Memory.Store, out var selectedMemory)
                          && selectedMemory is not null
            ? selectedMemory
            : memories;
        var actor = string.Equals(profile.History.Scope, "user", StringComparison.OrdinalIgnoreCase)
            ? user.Id
            : null;
        var key = new ConversationKey(source.Key, conversation.Id, conversation.GuildId, actor);
        var budget = new ContextBudget(
            profile.Context.MaxInputCharacters,
            profile.Context.MaxMemoryItems,
            profile.Context.MaxSummaryCharacters,
            profile.Context.MaxRecentMessages,
            profile.Context.MaxRecentCharacters);

        var windowSize = Math.Max(1, budget.MaxRecentMessages);
        var allMessages = await conversations.GetRecentAsync(
            key,
            Math.Max(windowSize * 2, windowSize + 10),
            Math.Max(budget.MaxRecentCharacters, budget.MaxRecentCharacters * 2),
            cancellationToken).ConfigureAwait(false);
        var recent = allMessages.Count > windowSize
            ? allMessages.TakeLast(windowSize).ToArray()
            : allMessages;

        IReadOnlyList<MemoryItem> memoryItems = [];
        string? fallback = null;
        try
        {
            memoryItems = await memoryStore.SearchAsync(
                new MemoryQuery(
                    source,
                    conversation,
                    user,
                    budget.MaxMemoryItems,
                    IncludePrivate: conversation.Kind is ConversationKind.Direct or ConversationKind.Group),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fallback = "memory:" + ex.GetType().Name;
        }

        ConversationSummary? summary = null;
        try
        {
            summary = await summaries.GetLatestAsync(key, cancellationToken).ConfigureAwait(false);
            if (allMessages.Count > windowSize)
            {
                var older = allMessages.Take(allMessages.Count - windowSize).ToArray();
                var lastCovered = older.LastOrDefault()?.MessageId ?? 0;
                if (summary is null || summary.ToMessageId < lastCovered)
                {
                    summary = BuildDeterministicSummary(
                        key,
                        older,
                        profile.Prompt.Version,
                        summary);
                    await summaries.SaveAsync(summary, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            fallback ??= "summary:" + ex.GetType().Name;
        }

        PromptBundle prompt;
        try
        {
            prompt = await promptProvider.GetAsync(
                new PromptRequest(profile, source, conversation, user, null, variables ?? new Dictionary<string, string>()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fallback ??= "prompt:" + ex.GetType().Name;
            prompt = new PromptBundle("You are Natter, a concise assistant.", null, "fallback");
        }

        try
        {
            return await contextAssembler.AssembleAsync(
                new ContextRequest(profile, prompt, source, conversation, user, userText, recent, memoryItems, summary, budget),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Last-resort recent-only context; never block the reply on policy failure.
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, prompt.SystemPrompt),
            };
            messages.AddRange(recent.Select(message => new ChatMessage(
                string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? ChatRole.Assistant
                    : ChatRole.User,
                message.Text)));
            messages.Add(new ChatMessage(ChatRole.User, userText));
            return new ContextBundle(
                messages,
                [],
                messages.Sum(message => message.Text?.Length ?? 0),
                "recent-only-fallback",
                new ContextDiagnostics(recent.Count, 0, false, 0, fallback ?? "assembler:" + ex.GetType().Name));
        }
    }
}
