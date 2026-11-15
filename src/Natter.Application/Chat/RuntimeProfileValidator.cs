namespace Natter.Application.Chat;

/// <summary>Startup policy: a missing Tier 1 provider fails the host; a missing Tier 2 provider disables that profile.</summary>
public static class RuntimeProfileValidator
{
    public static IReadOnlySet<string> Validate(IRuntimeProfileStore store, IChatResponderRegistry registry)
    {
        var set = store.Current;
        if (!set.IsExplicit)
            return new HashSet<string>(StringComparer.Ordinal);

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in set.Profiles)
        {
            if (registry.TryGet(profile.Ai.Provider, out _))
                continue;

            if (profile.SupportTier == 1)
            {
                throw new InvalidOperationException(
                    $"Runtime profile '{profile.Id}' requires Tier 1 provider '{profile.Ai.Provider}', but it is not loaded.");
            }

            disabled.Add(profile.Id);
        }
        return disabled;
    }
}
