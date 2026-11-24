using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Natter.Chat;

namespace Natter.Application.Chat;

public sealed record PromptRequest(
    RuntimeProfile Profile,
    Source Source,
    Conversation Conversation,
    User User,
    string? Locale,
    IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>
    /// Memory fragments for this turn (P6 §7). They appear in <see cref="PromptPlan.Fragments"/>,
    /// can be disabled through profile.prompt.disabledFragments and obey the provider quota.
    /// </summary>
    public IReadOnlyList<PromptFragment> MemoryFragments { get; init; } = [];
}

public sealed record PromptSection(string Id, string Text, int Order);

/// <summary>
/// Legacy provider output kept for compatibility. <see cref="PlanHash"/> / <see cref="Plan"/> are
/// filled by the Prompt Compiler. Safety is an ordinary fragment in the compiled prompt; there is
/// no host-appended suffix any more.
/// </summary>
public sealed record PromptBundle(
    string SystemPrompt,
    string? Persona,
    string Version,
    IReadOnlyList<PromptSection>? Sections = null)
{
    public string? PlanHash { get; init; }

    public PromptPlan? Plan { get; init; }

    /// <summary>Plugin fragments that reached the compiled plan (diagnostics only).</summary>
    public int PluginFragmentCount { get; init; }

    /// <summary>Number of <c>prompt.fragment</c> providers consulted for this prompt.</summary>
    public int PluginFragmentProviderCount { get; init; }

    /// <summary>Time spent aggregating plugin fragments (diagnostics only).</summary>
    public long PluginFragmentElapsedMilliseconds { get; init; }

    /// <summary>Enabled fragment metadata (id/source/order), never fragment text.</summary>
    public IReadOnlyList<PromptFragmentDiagnostic> FragmentDiagnostics { get; init; } = [];

    /// <summary>Disabled fragment metadata (id/source/order), never fragment text.</summary>
    public IReadOnlyList<PromptFragmentDiagnostic> DisabledFragmentDiagnostics { get; init; } = [];
}

public interface IPromptProvider
{
    ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default);
}

public sealed record ContextBudget(
    int MaxInputCharacters,
    int MaxMemoryItems,
    int MaxSummaryCharacters,
    int MaxRecentMessages,
    int MaxRecentCharacters);

public sealed record ContextRequest(
    RuntimeProfile Profile,
    PromptBundle Prompt,
    Source Source,
    Conversation Conversation,
    User User,
    string UserText,
    IReadOnlyList<StoredMessage> RecentMessages,
    IReadOnlyList<MemoryEntry> Memories,
    ConversationSummary? Summary,
    ContextBudget Budget);

public sealed record ContextDiagnostics(
    int RecentMessageCount,
    int MemoryCount,
    bool SummaryIncluded,
    int TrimmedCharacters,
    string? FallbackReason = null,
    string? PromptPlanHash = null,
    int PluginFragmentCount = 0,
    int PluginFragmentProviderCount = 0,
    long PluginFragmentElapsedMilliseconds = 0,
    IReadOnlyList<PromptFragmentDiagnostic>? PromptFragments = null,
    IReadOnlyList<PromptFragmentDiagnostic>? DisabledPromptFragments = null);

public sealed record ContextBundle(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> UsedMemoryIds,
    int EstimatedCharacters,
    string Version,
    ContextDiagnostics Diagnostics);

public interface IContextAssembler
{
    ValueTask<ContextBundle> AssembleAsync(ContextRequest request, CancellationToken cancellationToken = default);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryScopeType
{
    User,
    Conversation,
    Guild,
    Global,
}

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryItem(
    string MemoryId,
    MemoryScopeType ScopeType,
    string ScopeId,
    string? SubjectId,
    string Kind,
    string Text,
    string? DataJson,
    double Confidence,
    string Sensitivity,
    bool UserConfirmed,
    IReadOnlyList<string>? SourceMessageIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    int Version);

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryWriteRequest(
    Source Source,
    Conversation Conversation,
    User User,
    MemoryScopeType ScopeType,
    string Kind,
    string Text,
    string? DataJson = null,
    double Confidence = 1.0,
    string Sensitivity = "normal",
    bool UserConfirmed = true,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary>
    /// Write without an explicit scope: the profile.memory.scope default (actor/User unless the
    /// profile selects conversation/guild/global).
    /// </summary>
    public MemoryWriteRequest(Source source, Conversation conversation, User user, string kind, string text)
        : this(source, conversation, user, MemoryScope.DefaultWriteScope(ProfileContext.Current), kind, text)
    {
    }
}

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryQuery(
    Source Source,
    Conversation Conversation,
    User User,
    int Limit = 8,
    bool IncludePrivate = true)
{
    /// <summary>
    /// Retrieval text (P6). Empty trades relevance for recency (legacy behaviour).
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Scope filter. Null keeps the legacy "all visible scopes" behaviour so existing call sites
    /// do not change; the context pipeline passes the profile-configured scope (default actor).
    /// </summary>
    public MemoryScopeType? Scope { get; init; }

    /// <summary>
    /// Stability window for time decay, default 30 days. It only lowers the score — entries are
    /// never deleted here.
    /// <para>
    /// <b>Reading depends on <see cref="Mode"/>.</b> Under <see cref="DecayMode.Legacy"/> it is a true
    /// half-life: retention <c>exp(-age/S)</c> reaches 0.368 after one window. Under
    /// <see cref="DecayMode.PowerLaw"/> it is the interval at which retention is still
    /// <see cref="MemoryDecay.TargetRetention"/> (0.9). One field, two readings — which is why the
    /// mode is explicit rather than implied by the caller.
    /// </para>
    /// <para>
    /// Renamed from <c>DecayHalfLife</c> (S3 A-01): the old name asserted the v1 reading only, and the
    /// comment that justified keeping it ("61 call sites") was stale — the real count was four.
    /// </para>
    /// </summary>
    public TimeSpan StabilityWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Forgetting curve applied to this query. Defaults to the shipped v1 curve.</summary>
    public DecayMode Mode { get; init; } = DecayMode.Legacy;
}

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryDeleteRequest(
    Source Source,
    Conversation Conversation,
    User User,
    string? MemoryId = null,
    string? Text = null);

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryExportRequest(Source Source, Conversation Conversation, User User);

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryExport(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<MemoryItem> Items);

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public sealed record MemoryImportRequest(
    Source Source,
    Conversation Conversation,
    User User,
    MemoryExport Export,
    bool Overwrite = false);

    [Obsolete("v1 memory store retired at P6.7; use IMemoryEntryStore")]
public interface IMemoryStore
{
    ValueTask<MemoryItem> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken = default);

    ValueTask<MemoryExport> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken = default);

    ValueTask<int> ImportAsync(MemoryImportRequest request, CancellationToken cancellationToken = default);
}

public sealed record ConversationSummary(
    string SummaryId,
    ConversationKey Key,
    long FromMessageId,
    long ToMessageId,
    string Text,
    int Version,
    string PromptVersion,
    DateTimeOffset CreatedAt);

public interface ISummaryStore
{
    ValueTask<ConversationSummary?> GetLatestAsync(ConversationKey key, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(ConversationSummary summary, CancellationToken cancellationToken = default);

    ValueTask InvalidateAsync(ConversationKey key, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Host-owned prompt → summary/memory → context pipeline used by AI responders.</summary>
public interface IContextPipeline
{
    ValueTask<ContextBundle> AssembleAsync(
        Source source,
        Conversation conversation,
        User user,
        string userText,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default);
}
