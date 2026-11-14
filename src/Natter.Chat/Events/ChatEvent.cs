using System.Text.Json;

namespace Natter.Chat;

/// <summary>
/// Immutable fact pushed by an upstream. Events are not commands: consumers never use an
/// event to drive a platform API call by themselves.
/// </summary>
public abstract record ChatEvent
{
    public string Version { get; init; } = Nlmp.Version;

    /// <summary>Unique event id used for deduplication (at least within the replay window).</summary>
    public required string Id { get; init; }

    /// <summary>Dot-separated event type; unknown types must be ignored by consumers.</summary>
    public abstract string Type { get; }

    public required Source Source { get; init; }

    /// <summary>Monotonic only inside one source; never compare across sources.</summary>
    public long? Sequence { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    public override string ToString() => $"{Type} {Source.Key} #{Id}";
}

/// <summary>Typed NLMP event envelope.</summary>
public abstract record ChatEvent<TPayload> : ChatEvent
{
    public required TPayload Payload { get; init; }
}
