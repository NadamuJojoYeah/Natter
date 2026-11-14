using System.Text.Json;

namespace Natter.Chat;

/// <summary>
/// Operation issued by Natter to an upstream. In-process this is a method argument; the
/// JSON envelope is only an optional projection.
/// </summary>
public abstract record ChatCommand
{
    public string Version { get; init; } = Nlmp.Version;

    public required string Id { get; init; }

    public abstract string Type { get; }

    public required Source Source { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Deduplication key. Required for sends; retries MUST reuse it.</summary>
    public string? IdempotencyKey { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    public override string ToString() => $"{Type} {Source.Key} #{Id}";
}

/// <summary>Typed NLMP command envelope.</summary>
public abstract record ChatCommand<TPayload> : ChatCommand
{
    public required TPayload Payload { get; init; }
}
