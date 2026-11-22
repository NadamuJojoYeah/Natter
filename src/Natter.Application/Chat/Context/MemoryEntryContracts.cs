using System.Security.Cryptography;
using System.Text;
using Natter.Chat;

// ===================================================================================================
// P6.5 layered memory contract (docs/P6_MEMORY_SYSTEM.md §11).
//
// SCOPE GATE — read before consuming anything in this file:
//   `memory_entries` (the table these types describe) is NOT on the live path until P6.7.
//   The live chain is still: HostContextPipeline -> IMemoryStore -> `memory_items`.
//   Until P6.7 switches it, this file's types and <c>SqliteMemoryEntryStore</c> serve ONLY tests and
//   the v1 -> v2 migration. Wiring IMemoryEntryStore into the context pipeline early would split
//   reads and writes across two tables with different liveness semantics (`expires_at` hard expiry
//   vs `archived` soft archival), which is exactly the divergence this gate prevents.
//   Guard test: MemoryContractTests.Live_recall_path_still_uses_the_v1_contract
//   (delete that assertion in the same change that flips the write path).
//
//   Do not describe layered memory as "upgraded"/"shipped" in docs, logs or diagnostics until then.
// ===================================================================================================

namespace Natter.Application.Chat;

/// <summary>
/// Five memory kinds (docs/P6_MEMORY_SYSTEM.md §3). The type decides the initial stability,
/// the recall prior and whether the entry may be superseded.
/// </summary>
public enum MemoryType
{
    /// <summary>A constraint the user put on behaviour ("answer in Chinese"). Never enters system message.</summary>
    Policy,

    /// <summary>A taste or aversion. New value overwrites the old one.</summary>
    Preference,

    /// <summary>An objective fact about the user or the world. Superseded by a contradicting fact.</summary>
    Fact,

    /// <summary>Something that happened. Decays fast, reinforced when recalled.</summary>
    Episodic,

    /// <summary>Raw conversation turn. Indexed for lookup, never actively injected.</summary>
    Trace,
}

/// <summary>
/// Lifecycle facade over <see cref="MemoryDecay"/> (docs/P6_MEMORY_SYSTEM.md §5, §6, §9).
/// <para>
/// <b>P6.5 scope gate:</b> <c>memory_entries</c> — the table these types describe — serves
/// <b>tests and the v1 → v2 migration only</b> until P6.7 switches the write path. The live chain is
/// still <c>HostContextPipeline</c> → <see cref="IMemoryStore"/> → <c>memory_items</c>. Do not wire
/// <see cref="IMemoryEntryStore"/> into the pipeline before P6.7: that would split reads and writes
/// across two tables with different liveness semantics. <c>MemoryContractTests.Live_recall_path</c>
/// guards this and must be deleted together with the switch.
/// </para>
/// <para>
/// The forgetting maths lives in <see cref="MemoryDecay"/> (pure, power-law, FSRS-6 based). This
/// type carries no curve parameters of its own so there is exactly one place to calibrate.
/// </para>
/// </summary>
public static class MemoryStability
{
    /// <summary>
    /// β: recall is weak consolidation, so it grows stability less than a repeat (§6.1). No literature
    /// backing; our value, marked for calibration.
    /// <para>
    /// <b>Deliberately undamped:</b> this is a flat multiplier <c>S × (1+β)</c>, unlike
    /// <see cref="MemoryDecay.StabilityGrowth"/> which damps growth for stable entries. Repeated
    /// recalls would therefore grow stability without bound. Accepted for now because β is small and
    /// recalls are infrequent; <b>P6.9 must evaluate switching to
    /// <c>StabilityGrowth(S, 0.05)</c></b> before this becomes permanent design.
    /// </para>
    /// </summary>
    public const double RecallGrowth = 0.05;

    /// <summary>Initial stability in days for one type (§5.2), taken from the shared decay profile.</summary>
    public static double InitialDays(MemoryType type) => MemoryDecay.InitialStabilityDays(type);

    /// <summary>Decay exponent and initial stability for one type (§5.2).</summary>
    public static MemoryDecayProfile ProfileFor(MemoryType type) => MemoryDecay.ProfileFor(type);

    /// <summary>
    /// Retention of an entry (§5.1, power law). Delegates to <see cref="MemoryDecay.Retention"/>.
    /// </summary>
    public static double Decay(
        DateTimeOffset updatedAt,
        DateTimeOffset now,
        double stabilityDays,
        double decayD)
        => MemoryDecay.Retention(Math.Max(0, (now - updatedAt).TotalDays), stabilityDays, decayD);

    /// <summary>
    /// One repeat reinforcement (§6.1): returns the <b>next stability</b>,
    /// <c>S × (1 + α·S^-0.0658)</c>. Note <see cref="MemoryDecay.StabilityGrowth"/> returns only the
    /// multiplier, so the product happens here — passing the multiplier through would collapse
    /// stability to ~1.75 days.
    /// </summary>
    public static double Reinforce(double stabilityDays, double alpha = MemoryDecay.DefaultAlpha)
    {
        var stability = double.IsNaN(stabilityDays) || double.IsInfinity(stabilityDays) || stabilityDays <= 0d
            ? MemoryDecay.MinStabilityDays
            : stabilityDays;
        return stability * MemoryDecay.StabilityGrowth(stability, alpha);
    }

    /// <summary>
    /// Only these types are actively injected; <see cref="MemoryType.Trace"/> never is (§8.1).
    /// This is an injection rule, not a decay parameter.
    /// </summary>
    public static bool IsActivelyInjectable(MemoryType type) => type != MemoryType.Trace;
}

/// <summary>
/// One memory entry (docs/P6_MEMORY_SYSTEM.md §11). This is the v2 contract; the legacy
/// <see cref="MemoryItem"/> stays untouched so existing stores and callers keep working.
/// </summary>
public sealed record MemoryEntry(
    string Id,
    MemoryType Type,
    MemoryScopeType ScopeType,
    string ScopeId,
    string Content,
    string ContentHash,
    double StabilityDays,
    int ReinforceCount,
    double Confidence,
    bool Archived,
    string? SupersedesId,
    IReadOnlyList<string>? SourceMessageIds,
    string Sensitivity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRecalledAt)
{
    /// <summary>
    /// Current recall weight (§9.1): the power-law retention for this entry's type. There is no
    /// category multiplier — the per-type decay exponent already encodes the prior (§9.1.1).
    /// </summary>
    public double WeightAt(DateTimeOffset now)
        => MemoryStability.Decay(UpdatedAt, now, StabilityDays, MemoryDecay.ProfileFor(Type).DecayD);
}

public sealed record MemoryEntryWriteRequest(
    Source Source,
    Conversation Conversation,
    User User,
    MemoryType Type,
    string Content,
    MemoryScopeType? Scope = null,
    double Confidence = 1.0,
    string Sensitivity = "normal",
    IReadOnlyList<string>? SourceMessageIds = null);

public sealed record MemoryEntryQuery(
    Source Source,
    Conversation Conversation,
    User User)
{
    /// <summary>Retrieval text. Empty means "most recent first".</summary>
    public string? Query { get; init; }

    /// <summary>Types to include; null means every actively injectable type.</summary>
    public IReadOnlyList<MemoryType>? Types { get; init; }

    /// <summary>Explicit scope filter. Null keeps the visibility union (legacy behaviour).</summary>
    public MemoryScopeType? Scope { get; init; }

    /// <summary>Archived entries are excluded by default; the tool path may include them (§5.3).</summary>
    public bool IncludeArchived { get; init; }

    public bool IncludePrivate { get; init; } = true;

    public int Limit { get; init; } = 20;
}

/// <summary>v2 memory store contract. Separate from <see cref="IMemoryStore"/> on purpose: the v1
/// contract is still consumed by the CLI and the context pipeline.</summary>
public interface IMemoryEntryStore
{
    ValueTask<MemoryEntry> WriteAsync(MemoryEntryWriteRequest request, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MemoryEntry>> SearchAsync(MemoryEntryQuery query, CancellationToken cancellationToken = default);

    ValueTask<MemoryEntry?> GetAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>Archives instead of deleting (§5.3). Returns false when the entry is not visible.</summary>
    ValueTask<bool> ArchiveAsync(
        Source source,
        Conversation conversation,
        User user,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>Records a successful recall and applies the recall reinforcement (§6.1).</summary>
    ValueTask ReinforceRecallAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>Mapping and normalization shared by both stores, so the v1 → v2 migration is testable.</summary>
public static class MemoryEntryMapping
{
    /// <summary>
    /// Maps a legacy free-form <c>kind</c> to a <see cref="MemoryType"/>. Unknown kinds become
    /// <see cref="MemoryType.Fact"/>: its initial stability (90d) is the most conservative of the
    /// long-lived types, and a Fact is only ever superseded by an explicit contradicting fact.
    /// </summary>
    public static MemoryType FromLegacyKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "policy" => MemoryType.Policy,
            "preference" => MemoryType.Preference,
            "fact" => MemoryType.Fact,
            "episodic" or "episode" or "event" => MemoryType.Episodic,
            "trace" or "turn" or "message" => MemoryType.Trace,
            _ => MemoryType.Fact,
        };

    public static string ToStorageValue(MemoryType type) => type.ToString().ToLowerInvariant();

    public static bool TryParseStorageValue(string? value, out MemoryType type)
    {
        type = MemoryType.Fact;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Enum.TryParse(value, ignoreCase: true, out type) && Enum.IsDefined(type);
    }

    /// <summary>Normalizes content for hashing: trimmed, internal whitespace collapsed, case-folded.</summary>
    public static string Normalize(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var builder = new StringBuilder(content.Length);
        var pendingSpace = false;
        foreach (var character in content.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }

        return builder.ToString().ToLowerInvariant();
    }

    /// <summary><c>sha256(type | scopeId | normalized content)</c> — the repeat-detection key (§6.2).</summary>
    public static string Hash(MemoryType type, string scopeId, string content)
    {
        ArgumentNullException.ThrowIfNull(scopeId);
        var payload = string.Concat(ToStorageValue(type), "\u001f", scopeId, "\u001f", Normalize(content));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>
/// Export envelope version policy (docs/P6_MEMORY_SYSTEM.md §11). Reading tolerates one version
/// back; writing always emits the current version; a newer envelope is refused loudly instead of
/// silently dropping the fields this build does not know about.
/// </summary>
public static class MemoryExportVersions
{
    /// <summary>Version written by this build.</summary>
    public const int Current = 2;

    /// <summary>Oldest version this build can still read.</summary>
    public const int MinimumReadable = 1;

    /// <summary>Error code surfaced when an envelope is newer than this build understands.</summary>
    public const string TooNewCode = "memory.export_version_too_new";

    /// <summary>
    /// Returns true when <paramref name="version"/> can be read. Never clamps: an unreadable
    /// envelope must be rejected by the caller.
    /// </summary>
    public static bool IsReadable(int version) => version is >= MinimumReadable and <= Current;

    /// <summary>True when the envelope declares a version newer than this build writes.</summary>
    public static bool IsTooNew(int version) => version > Current;

    /// <summary>
    /// Validates a version read from storage. Throws <see cref="MemoryExportVersionException"/>
    /// rather than downgrading silently.
    /// </summary>
    public static void EnsureReadable(int version)
    {
        if (IsReadable(version))
            return;

        throw new MemoryExportVersionException(version);
    }
}

/// <summary>Raised when an export envelope cannot be read by this build.</summary>
public sealed class MemoryExportVersionException(int version)
    : InvalidOperationException(
        $"{MemoryExportVersions.TooNewCode}: memory export schemaVersion {version} is not readable by this build " +
        $"(supported {MemoryExportVersions.MinimumReadable}..{MemoryExportVersions.Current}). Refusing instead of dropping fields.")
{
    public int ExportVersion { get; } = version;

    public string Code => MemoryExportVersions.TooNewCode;
}

/// <summary>Versioned v2 export envelope.</summary>
public sealed record MemoryEntryExport(
    int SchemaVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<MemoryEntry> Items)
{
    /// <summary>Returns the envelope after validating its version.</summary>
    public MemoryEntryExport EnsureReadable()
    {
        MemoryExportVersions.EnsureReadable(SchemaVersion);
        return this;
    }

    /// <summary>Creates a current-version envelope.</summary>
    public static MemoryEntryExport Create(IReadOnlyList<MemoryEntry> items, DateTimeOffset exportedAt)
        => new(MemoryExportVersions.Current, exportedAt, items);
}

/// <summary>
/// Upgrades a v1 <see cref="MemoryExport"/> (flat <see cref="MemoryItem"/> rows) to the current v2
/// envelope. This is what "read one version back" concretely means for memory: an old backup stays
/// restorable, and the field mapping is a pure function so it can be asserted without a database.
/// </summary>
public static class MemoryExportBridge
{
    /// <summary>Converts v1 items to v2 entries. Unreadable envelopes are refused, not patched.</summary>
    public static MemoryEntryExport Upgrade(MemoryExport legacy)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        MemoryExportVersions.EnsureReadable(legacy.SchemaVersion);

        var items = new List<MemoryEntry>(legacy.Items.Count);
        foreach (var item in legacy.Items)
        {
            var type = MemoryEntryMapping.FromLegacyKind(item.Kind);

            // v1 deleted by expiry; v2 archives (§5.3), so an expired row becomes archived.
            var archived = item.ExpiresAt is { } expiresAt && expiresAt <= legacy.ExportedAt;

            items.Add(new MemoryEntry(
                item.MemoryId,
                type,
                item.ScopeType,
                item.ScopeId,
                item.Text,
                MemoryEntryMapping.Hash(type, item.ScopeId, item.Text),
                MemoryStability.InitialDays(type),
                ReinforceCount: 0,
                item.Confidence,
                archived,
                SupersedesId: null,
                item.SourceMessageIds,
                item.Sensitivity,
                item.CreatedAt,
                item.UpdatedAt,
                LastRecalledAt: null));
        }

        return MemoryEntryExport.Create(items, legacy.ExportedAt);
    }
}
