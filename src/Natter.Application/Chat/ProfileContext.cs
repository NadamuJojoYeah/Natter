namespace Natter.Application.Chat;

/// <summary>
/// Ambient profile for the duration of one responder call. Provider adapters read history
/// scope/limits from here so profile policy is not baked into plugin config.
/// </summary>
public static class ProfileContext
{
    private static readonly AsyncLocal<RuntimeProfile?> CurrentProfile = new();

    public static RuntimeProfile? Current => CurrentProfile.Value;

    public static IDisposable Push(RuntimeProfile profile)
    {
        var previous = CurrentProfile.Value;
        CurrentProfile.Value = profile;
        return new Scope(previous);
    }

    private sealed class Scope(RuntimeProfile? previous) : IDisposable
    {
        public void Dispose() => CurrentProfile.Value = previous;
    }
}
