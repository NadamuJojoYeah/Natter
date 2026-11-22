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
    IReadOnlyDictionary<string, string> Variables);

public sealed record PromptSection(string Id, string Text, int Order);

/// <summary>
/// Legacy provider output kept for compatibility. <see cref="PlanHash"/> / <see cref="Plan"/> are
/// filled by the Prompt Compiler; <see cref="SafetyAppended"/> tells the context assembler that the
/// host mandatory safety block is already part of <see cref="SystemPrompt"/>.
/// </summary>
public sealed record PromptBundle(
    string SystemPrompt,
    string? Persona,
    string Version,
    IReadOnlyList<PromptSection>? Sections = null)
{
    public string? PlanHash { get; init; }

    public PromptPlan? Plan { get; init; }

    public bool SafetyAppended { get; init; }

    /// <summary>Plugin fragments that reached the compiled plan (diagnostics only).</summary>
    public int PluginFragmentCount { get; init; }

    /// <summary>Number of <c>prompt.fragment</c> providers consulted for this prompt.</summary>
    public int PluginFragmentProviderCount { get; init; }
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
    IReadOnlyList<MemoryItem> Memories,
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
    int PluginFragmentProviderCount = 0);

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
    DateTimeOffset? ExpiresAt = null);

public sealed record MemoryQuery(
    Source Source,
    Conversation Conversation,
    User User,
    int Limit = 8,
    bool IncludePrivate = true);

public sealed record MemoryDeleteRequest(
    Source Source,
    Conversation Conversation,
    User User,
    string? MemoryId = null,
    string? Text = null);

public sealed record MemoryExportRequest(Source Source, Conversation Conversation, User User);

public sealed record MemoryExport(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<MemoryItem> Items);

public sealed record MemoryImportRequest(
    Source Source,
    Conversation Conversation,
    User User,
    MemoryExport Export,
    bool Overwrite = false);

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
