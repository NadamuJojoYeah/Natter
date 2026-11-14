namespace Natter.Chat;

/// <summary>Author kind. Consumers must not treat <see cref="Unknown"/> as a human by default.</summary>
public enum UserKind
{
    Human,
    Bot,
    System,
    Unknown,
}

/// <summary>Minimal platform user projection (no roles, permissions or join metadata in v0.1).</summary>
public sealed record User(string Id, string Name, UserKind Kind = UserKind.Unknown, string? DisplayName = null, string? AvatarUrl = null)
{
    public static User System(string name = "system") => new("system", name, UserKind.System);
}
