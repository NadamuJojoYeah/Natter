using System.Text.Json;

namespace Natter.Chat;

/// <summary>Interaction trigger kind.</summary>
public enum InteractionKind
{
    Slash,
    Button,
    Select,
    Modal,
    Unknown,
}

/// <summary>
/// Slash command / button / selector / modal interaction. v0.1 keeps only the fields an
/// AI feature needs; modal forms live in <see cref="Extensions"/>.
/// </summary>
public sealed record Interaction(
    string Id,
    InteractionKind Kind,
    User User,
    Conversation Conversation,
    DateTimeOffset CreatedAt,
    string? Command = null,
    IReadOnlyDictionary<string, JsonElement>? Options = null,
    IReadOnlyList<string>? Values = null,
    string? ReplyToken = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null)
{
    /// <summary>Never let the short-lived reply token leak through logs.</summary>
    public override string ToString()
        => $"Interaction {{ Id = {Id}, Kind = {Kind}, Command = {Command}, ReplyToken = {(ReplyToken is null ? "null" : "***")} }}";
}
