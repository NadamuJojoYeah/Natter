using System.Text.Json;
using System.Text.Json.Serialization;
using Natter.Chat;

namespace Natter.Application.Chat;

public sealed class RuntimeProfileSet
{
    public int SchemaVersion { get; init; } = 1;

    public string DefaultProfile { get; init; } = "tier1-default";

    public List<RuntimeProfile> Profiles { get; init; } = [];

    [JsonIgnore]
    public bool IsExplicit { get; init; }

    [JsonIgnore]
    public string? OverrideProfile { get; init; }
}

public sealed class RuntimeProfile
{
    public string Id { get; init; } = string.Empty;

    public int SupportTier { get; init; } = 1;

    public ProfileMatch Match { get; init; } = new();

    public ProfileAi Ai { get; init; } = new();

    public ProfileHistory History { get; init; } = new();

    public ProfilePrompt Prompt { get; init; } = new();

    public ProfileContextSettings Context { get; init; } = new();

    public ProfileMemory Memory { get; init; } = new();

    public ProfilePolicy Policy { get; init; } = new();
}

public sealed class ProfileMatch
{
    public List<string> Protocols { get; init; } = [];

    public List<string> Platforms { get; init; } = [];

    public List<string> Guilds { get; init; } = [];

    public List<string> Conversations { get; init; } = [];

    public List<string> Actors { get; init; } = [];
}

public sealed class ProfileAi
{
    public string Provider { get; init; } = string.Empty;

    public string? Model { get; init; }
}

public sealed class ProfilePrompt
{
    public string Provider { get; init; } = "host#static";

    public string Version { get; init; } = "default";

    public string? SystemPrompt { get; init; }
}

public sealed class ProfileContextSettings
{
    public string Assembler { get; init; } = "host#recent-window";

    public int MaxInputCharacters { get; init; } = 12000;

    public int MaxMemoryItems { get; init; } = 8;

    public int MaxSummaryCharacters { get; init; } = 1200;

    public int MaxRecentMessages { get; init; } = 20;

    public int MaxRecentCharacters { get; init; } = 24000;
}

public sealed class ProfileMemory
{
    public string Store { get; init; } = "host#inline";

    public string Retriever { get; init; } = "host#recent";

    public string Extractor { get; init; } = "host#explicit";
}

public sealed class ProfileHistory
{
    public string Scope { get; init; } = "conversation";

    public int MaxMessages { get; init; } = 20;

    public int MaxCharacters { get; init; } = 24000;
}

public sealed class ProfilePolicy
{
    public bool Enabled { get; init; } = true;
}

public interface IRuntimeProfileStore
{
    RuntimeProfileSet Current { get; }

    ValueTask ReloadAsync(CancellationToken cancellationToken = default);
}

public interface IProfileResolver
{
    RuntimeProfile? Resolve(Source source, Conversation conversation, User sender);
}

/// <summary>Loads profiles.json and supplies safe Tier 1/Tier 2 defaults when absent.</summary>
public sealed class RuntimeProfileStore : IRuntimeProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string? _path;
    private readonly string? _profileOverride;
    private readonly Lock _gate = new();
    private RuntimeProfileSet _current;

    public RuntimeProfileStore(string? configDirectory, string? profileOverride = null)
    {
        _path = string.IsNullOrWhiteSpace(configDirectory)
            ? null
            : Path.Combine(configDirectory, "profiles.json");
        _profileOverride = string.IsNullOrWhiteSpace(profileOverride) ? null : profileOverride;
        _current = ApplyOverride(Load(_path));
    }

    public RuntimeProfileSet Current
    {
        get { lock (_gate) return _current; }
    }

    public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = ApplyOverride(Load(_path));
        lock (_gate)
            _current = loaded;
        return ValueTask.CompletedTask;
    }

    private RuntimeProfileSet ApplyOverride(RuntimeProfileSet set)
    {
        if (_profileOverride is null)
            return set;
        if (set.Profiles.All(profile => !string.Equals(profile.Id, _profileOverride, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Profile override '{_profileOverride}' does not exist.");
        return new RuntimeProfileSet
        {
            SchemaVersion = set.SchemaVersion,
            DefaultProfile = set.DefaultProfile,
            Profiles = set.Profiles,
            IsExplicit = set.IsExplicit,
            OverrideProfile = _profileOverride,
        };
    }

    private static RuntimeProfileSet Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return Defaults();

        RuntimeProfileSet? set;
        try
        {
            set = JsonSerializer.Deserialize<RuntimeProfileSet>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Runtime profiles '{path}' are invalid: {ex.Message}", ex);
        }

        if (set is null)
            throw new InvalidOperationException($"Runtime profiles '{path}' are empty.");

        Validate(set, path);
        return new RuntimeProfileSet
        {
            SchemaVersion = set.SchemaVersion,
            DefaultProfile = set.DefaultProfile,
            Profiles = set.Profiles,
            IsExplicit = true,
        };
    }

    private static void Validate(RuntimeProfileSet set, string path)
    {
        if (set.SchemaVersion != 1)
            throw new InvalidOperationException($"Runtime profiles '{path}' use unsupported schemaVersion {set.SchemaVersion}.");
        if (set.Profiles.Count == 0)
            throw new InvalidOperationException($"Runtime profiles '{path}' contain no profiles.");
        if (set.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != set.Profiles.Count)
            throw new InvalidOperationException($"Runtime profiles '{path}' contain duplicate ids.");
        if (string.IsNullOrWhiteSpace(set.DefaultProfile)
            || set.Profiles.All(profile => !string.Equals(profile.Id, set.DefaultProfile, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Runtime profiles '{path}' defaultProfile '{set.DefaultProfile}' does not exist.");

        var defaultProfile = set.Profiles.Single(profile => string.Equals(profile.Id, set.DefaultProfile, StringComparison.Ordinal));
        if (!defaultProfile.Policy.Enabled)
            throw new InvalidOperationException($"Runtime profiles '{path}' defaultProfile '{set.DefaultProfile}' must be enabled.");

        foreach (var profile in set.Profiles)
        {
            if (profile.SupportTier is not (1 or 2))
                throw new InvalidOperationException($"Profile '{profile.Id}' supportTier must be 1 or 2.");
            if (string.IsNullOrWhiteSpace(profile.Ai.Provider)
                || profile.Ai.Provider.IndexOf('#', StringComparison.Ordinal) <= 0)
                throw new InvalidOperationException($"Profile '{profile.Id}' ai.provider must be 'pluginId#responderId'.");
            if (!IsRef(profile.Prompt.Provider) || !IsRef(profile.Context.Assembler) || !IsRef(profile.Memory.Store))
                throw new InvalidOperationException($"Profile '{profile.Id}' prompt/context/memory refs must use 'owner#id'.");
            if (profile.Context.MaxInputCharacters <= 0
                || profile.Context.MaxMemoryItems < 0
                || profile.Context.MaxSummaryCharacters < 0
                || profile.Context.MaxRecentMessages < 0
                || profile.Context.MaxRecentCharacters < 0)
                throw new InvalidOperationException($"Profile '{profile.Id}' context budgets are invalid.");
            if (profile.History.Scope is not ("conversation" or "user"))
                throw new InvalidOperationException($"Profile '{profile.Id}' history.scope must be conversation or user.");
            if (profile.Match.Protocols is null || profile.Match.Platforms is null
                || profile.Match.Guilds is null || profile.Match.Conversations is null || profile.Match.Actors is null)
                throw new InvalidOperationException($"Profile '{profile.Id}' match lists must not be null.");
        }
    }

    private static bool IsRef(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.IndexOf('#', StringComparison.Ordinal) > 0
           && value.IndexOf('#', StringComparison.Ordinal) < value.Length - 1;

    private static RuntimeProfileSet Defaults() => new()
    {
        SchemaVersion = 1,
        DefaultProfile = "tier1-default",
        Profiles =
        [
            new RuntimeProfile
            {
                Id = "tier1-default",
                SupportTier = 1,
                Match = new ProfileMatch { Protocols = ["discord"], Platforms = ["discord"] },
                Ai = new ProfileAi { Provider = "natter.feature.ai.deepseek#deepseek" },
            },
            new RuntimeProfile
            {
                Id = "tier2-dingtalk-spark",
                SupportTier = 2,
                Match = new ProfileMatch { Protocols = ["dingtalk"], Platforms = ["dingtalk"] },
                Ai = new ProfileAi { Provider = "natter.feature.ai.spark#spark" },
            },
        ],
    };
}

/// <summary>Specificity-ordered profile resolver; array order is the final tie-breaker.</summary>
public sealed class ProfileResolver(IRuntimeProfileStore store) : IProfileResolver
{
    private readonly IRuntimeProfileStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public RuntimeProfile? Resolve(Source source, Conversation conversation, User sender)
    {
        var set = _store.Current;
        if (set.OverrideProfile is { } overrideId)
        {
            return set.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, overrideId, StringComparison.Ordinal));
        }

        var profiles = set.Profiles
            .Select((profile, index) => (Profile: profile, Index: index, Score: Score(profile, source, conversation, sender)))
            .Where(item => item.Score >= 0 && item.Profile.Policy.Enabled)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Profile)
            .ToList();

        if (profiles.Count > 0)
            return profiles[0];

        return set.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, set.OverrideProfile ?? set.DefaultProfile, StringComparison.Ordinal));
    }

    private static int Score(RuntimeProfile profile, Source source, Conversation conversation, User sender)
    {
        var match = profile.Match;
        if (!Matches(match.Protocols, source.Protocol)
            || !Matches(match.Platforms, source.Platform)
            || !Matches(match.Guilds, conversation.GuildId)
            || !Matches(match.Conversations, conversation.Id)
            || !Matches(match.Actors, sender.Id))
        {
            return -1;
        }

        var score = 0;
        if (match.Conversations.Count > 0)
            score += 16;
        if (match.Guilds.Count > 0)
            score += 8;
        if (match.Protocols.Count > 0)
            score += 4;
        if (match.Platforms.Count > 0)
            score += 2;
        if (match.Actors.Count > 0)
            score += 1;
        return score;
    }

    private static bool Matches(List<string> values, string? actual)
        => values.Count == 0
           || values.Contains("*", StringComparer.Ordinal)
           || (actual is not null && values.Contains(actual, StringComparer.Ordinal));
}
