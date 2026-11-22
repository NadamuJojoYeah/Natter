using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    IContextComponentCatalog? components = null,
    MemoryRetrievalOptions? memoryOptions = null,
    MemoryRetrievalMetrics? memoryMetrics = null,
    Microsoft.Extensions.Logging.ILogger<HostContextPipeline>? logger = null) : IContextPipeline
{
    private readonly MemoryRetrievalOptions _memoryOptions = memoryOptions ?? MemoryRetrievalOptions.Default;
    private readonly MemoryRetrievalMetrics _memoryMetrics = memoryMetrics ?? new MemoryRetrievalMetrics();

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
        var memoryScope = ResolveMemoryScope(profile, _memoryOptions);
        try
        {
            // P6 §8.1: memory is an enhancement. A 300ms timeout returns no memory and counts the
            // degradation instead of blocking the turn.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_memoryOptions.RetrievalTimeout);
            memoryItems = await memoryStore.SearchAsync(
                new MemoryQuery(
                    source,
                    conversation,
                    user,
                    _memoryOptions.MaxCandidates,
                    IncludePrivate: conversation.Kind is ConversationKind.Direct or ConversationKind.Group)
                {
                    Query = userText,
                    Scope = memoryScope,
                    DecayHalfLife = _memoryOptions.DecayHalfLife,
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _memoryMetrics.Timeout();
            logger?.LogWarning(
                "memory retrieval timed out after {TimeoutMs}ms; continuing without memory.",
                _memoryOptions.RetrievalTimeout.TotalMilliseconds);
            memoryItems = [];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            fallback ??= "summary:" + ex.GetType().Name;
        }

        // §7: memory is visible in the prompt plan. It is rendered as a dedicated untrusted-data
        // message by the assembler (never inside the system message), so the default output stays
        // byte-identical. disabledFragments removes both diagnostics and the injected message.
        var disabledMemory = profile.Prompt.DisabledFragments ?? [];
        var memoryFragments = BuildMemoryFragments(summary, memoryItems, profile)
            .Where(fragment => !disabledMemory.Contains(fragment.Id, StringComparer.Ordinal))
            .ToArray();
        if (disabledMemory.Contains("host#memory.summary", StringComparer.Ordinal))
            summary = null;
        if (memoryFragments.Length == 0)
            memoryItems = [];

        PromptBundle prompt;
        try
        {
            prompt = await promptProvider.GetAsync(
                new PromptRequest(profile, source, conversation, user, null, variables ?? new Dictionary<string, string>())
                {
                    MemoryFragments = memoryFragments,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
                new ContextDiagnostics(
                    recent.Count,
                    0,
                    false,
                    0,
                    fallback ?? "assembler:" + ex.GetType().Name,
                    prompt.PlanHash,
                    prompt.PluginFragmentCount,
                    prompt.PluginFragmentProviderCount,
                    prompt.PluginFragmentElapsedMilliseconds,
                    prompt.FragmentDiagnostics,
                    prompt.DisabledFragmentDiagnostics));
        }
    }

    private static MemoryScopeType? ResolveMemoryScope(RuntimeProfile profile, MemoryRetrievalOptions options)
    {
        if (string.IsNullOrWhiteSpace(profile.Memory.Scope))
            return null; // legacy all-visible behaviour

        return profile.Memory.Scope.Trim().ToLowerInvariant() switch
        {
            "conversation" => MemoryScopeType.Conversation,
            "guild" => MemoryScopeType.Guild,
            "global" => MemoryScopeType.Global,
            "actor" or "user" => MemoryScopeType.User,
            _ => options.DefaultScope,
        };
    }

    private IReadOnlyList<PromptFragment> BuildMemoryFragments(
        ConversationSummary? summary,
        IReadOnlyList<MemoryItem> items,
        RuntimeProfile profile)
    {
        var fragments = new List<PromptFragment>();
        var order = 200;
        if (summary is not null && !string.IsNullOrWhiteSpace(summary.Text))
        {
            fragments.Add(new PromptFragment(
                "host#memory.summary",
                "[Memory summary]\n" + summary.Text,
                PromptFragmentSource.Memory,
                Order: order,
                EnabledByDefault: true,
                MaxCharacters: PromptCompilerDefaults.FragmentMaxCharacters));
        }

        order = 210;
        var injected = 0;
        var tokens = 0;
        foreach (var item in items)
        {
            if (injected >= _memoryOptions.MaxInjected)
            {
                _memoryMetrics.InjectionTruncated();
                break;
            }

            var text = "[Memory]\n- " + item.Text;
            var estimate = MemoryTokens.Estimate(text);
            if (tokens + estimate > _memoryOptions.MaxTokens)
            {
                _memoryMetrics.TokenTruncated();
                break;
            }

            tokens += estimate;
            injected++;
            fragments.Add(new PromptFragment(
                "host#memory.recall." + injected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                text,
                PromptFragmentSource.Memory,
                Order: order++,
                EnabledByDefault: true,
                MaxCharacters: PromptCompilerDefaults.FragmentMaxCharacters));
        }

        return fragments;
    }
}
