namespace Natter.Application.Chat;

/// <summary>Startup policy: a missing Tier 1 provider fails the host; a missing Tier 2 provider disables that profile.</summary>
public static class RuntimeProfileValidator
{
    public static IReadOnlySet<string> Validate(IRuntimeProfileStore store, IChatResponderRegistry registry)
        => Validate(store, registry, null);

    public static IReadOnlySet<string> Validate(
        IRuntimeProfileStore store,
        IChatResponderRegistry registry,
        IContextComponentCatalog? components)
    {
        var set = store.Current;
        if (!set.IsExplicit)
            return new HashSet<string>(StringComparer.Ordinal);

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in set.Profiles)
        {
            var missingComponent = components is not null
                                   && (!components.TryGetPrompt(profile.Prompt.Provider, out _)
                                       || !components.TryGetAssembler(profile.Context.Assembler, out _)
                                       || !components.TryGetMemory(profile.Memory.Store, out _));
            if (missingComponent)
            {
                if (profile.SupportTier == 1 || set.OverrideProfile is not null)
                    throw new InvalidOperationException(
                        $"Runtime profile '{profile.Id}' references a missing prompt/context/memory component.");
                disabled.Add(profile.Id);
                continue;
            }

            if (registry.TryGet(profile.Ai.Provider, out _))
                continue;

            if (profile.SupportTier == 1 || set.OverrideProfile is not null)
            {
                throw new InvalidOperationException(
                    $"Runtime profile '{profile.Id}' requires provider '{profile.Ai.Provider}', but it is not loaded.");
            }

            disabled.Add(profile.Id);
        }
        return disabled;
    }
}
