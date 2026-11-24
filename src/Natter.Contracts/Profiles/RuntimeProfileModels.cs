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

    /// <summary>Preset id selected inside the prompt provider (Prompt Compiler P2). Optional.</summary>
    public string? Preset { get; init; }

    /// <summary>Locale used for preset selection when the runtime request has no locale.</summary>
    public string? Locale { get; init; }

    /// <summary>Profile-level fragment overrides; disabling never deletes the fragment source.</summary>
    public List<string> DisabledFragments { get; init; } = [];

    /// <summary>Preset variable defaults (only whitelisted names are substituted at compile time).</summary>
    public Dictionary<string, string> Variables { get; init; } = new(StringComparer.Ordinal);
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

    /// <summary>
    /// P6 default scope for NEW memories: actor (User, default) | conversation | guild | global.
    /// Retrieval visibility remains the union of all visible scopes; Query.Scope only narrows
    /// when a caller asks for an explicit filter.
    /// </summary>
    public string? Scope { get; init; } = "actor";

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

    public bool AllowNetworkTools { get; init; }
}

