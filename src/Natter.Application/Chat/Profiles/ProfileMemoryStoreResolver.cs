using Natter.Chat;
namespace Natter.Application.Chat;

public interface IProfileMemoryStoreResolver
{
    IMemoryEntryStore Resolve(Source source, Conversation conversation, User user);
}

/// <summary>Resolves the memory store selected by the active profile; never silently falls back for explicit Tier 1 refs.</summary>
public sealed class ProfileMemoryStoreResolver(
    IRuntimeProfileStore profiles,
    IProfileResolver resolver,
    IContextComponentCatalog components,
    IMemoryEntryStore hostDefault) : IProfileMemoryStoreResolver
{
    public IMemoryEntryStore Resolve(Source source, Conversation conversation, User user)
    {
        var profile = resolver.Resolve(source, conversation, user);
        if (profile is null)
            return hostDefault;

        if (components.TryGetMemory(profile.Memory.Store, out var store) && store is not null)
            return store;

        if (!profiles.Current.IsExplicit)
            return hostDefault;

        throw new InvalidOperationException(
            $"Profile '{profile.Id}' references missing memory.store '{profile.Memory.Store}'. "
            + "Enable the owning plugin or change the profile to host#sqlite; no silent fallback is performed.");
    }
}
