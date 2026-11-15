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
            var missingComponents = new List<string>();
            if (components is not null && !components.TryGetPrompt(profile.Prompt.Provider, out _))
                missingComponents.Add($"prompt.provider={profile.Prompt.Provider}");
            if (components is not null && !components.TryGetAssembler(profile.Context.Assembler, out _))
                missingComponents.Add($"context.assembler={profile.Context.Assembler}");
            if (components is not null && !components.TryGetMemory(profile.Memory.Store, out _))
                missingComponents.Add($"memory.store={profile.Memory.Store}");
            if (missingComponents.Count > 0)
            {
                if (profile.SupportTier == 1 || set.OverrideProfile is not null)
                {
                    throw new InvalidOperationException(
                        $"Runtime profile '{profile.Id}' references missing component(s): "
                        + string.Join(", ", missingComponents)
                        + ". Enable the owning plugin or change memory.store to host#sqlite; "
                        + "silent fallback is disabled.");
                }
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
