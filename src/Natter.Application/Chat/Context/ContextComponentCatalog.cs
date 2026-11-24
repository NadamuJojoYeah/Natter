namespace Natter.Application.Chat;

/// <summary>
/// Defers construction of a prompt provider until first use. The catalog is built before the
/// prompt pipeline and must not eagerly resolve <see cref="IPromptProvider"/> (which itself depends
/// on the fragment aggregator and therefore on the catalog).
/// </summary>
public sealed class DeferredPromptProvider(Func<IPromptProvider> factory) : IPromptProvider
{
    private readonly Lazy<IPromptProvider> _provider = new(
        factory ?? throw new ArgumentNullException(nameof(factory)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public ValueTask<PromptBundle> GetAsync(PromptRequest request, CancellationToken cancellationToken = default)
        => _provider.Value.GetAsync(request, cancellationToken);
}

/// <summary>Host registry for selectable prompt/context/memory components.</summary>
public interface IContextComponentCatalog
{
    void RegisterPrompt(string reference, IPromptProvider provider);

    void RegisterAssembler(string reference, IContextAssembler assembler);

    void RegisterMemory(string reference, IMemoryEntryStore store);

    /// <summary>Registers an NPS <c>prompt.fragment</c> provider under <c>owner#id</c>.</summary>
    void RegisterFragmentProvider(string reference, IPromptFragmentProvider provider);

    void UnregisterFragmentProvider(string reference);

    bool TryGetPrompt(string reference, out IPromptProvider? provider);

    bool TryGetAssembler(string reference, out IContextAssembler? assembler);

    bool TryGetMemory(string reference, out IMemoryEntryStore? store);

    /// <summary>Snapshot of registered fragment providers (unsorted; the aggregator sorts by reference).</summary>
    IReadOnlyList<(string Reference, IPromptFragmentProvider Provider)> FragmentProviders { get; }

    IReadOnlyList<ContextComponentDescriptor> Components { get; }

    void Unregister(string reference);
}

public sealed record ContextComponentDescriptor(string Owner, string Source, string Type, string Id);

public sealed class ContextComponentCatalog : IContextComponentCatalog
{
    private readonly Dictionary<string, IPromptProvider> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IContextAssembler> _assemblers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IMemoryEntryStore> _memories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPromptFragmentProvider> _fragmentProviders = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<ContextComponentDescriptor> _components = [];

    public IReadOnlyList<ContextComponentDescriptor> Components
    {
        get { lock (_gate) return _components.ToArray(); }
    }

    public void RegisterPrompt(string reference, IPromptProvider provider)
    {
        lock (_gate)
        {
            _prompts[reference] = provider;
            AddDescriptor(reference, "prompt.provider");
        }
    }

    public void RegisterAssembler(string reference, IContextAssembler assembler)
    {
        lock (_gate)
        {
            _assemblers[reference] = assembler;
            AddDescriptor(reference, "context.assembler");
        }
    }

    public void RegisterMemory(string reference, IMemoryEntryStore store)
    {
        lock (_gate)
        {
            _memories[reference] = store;
            AddDescriptor(reference, "memory.store");
        }
    }

    public void RegisterFragmentProvider(string reference, IPromptFragmentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            _fragmentProviders[reference] = provider;
            AddDescriptor(reference, "prompt.fragment");
        }
    }

    public void UnregisterFragmentProvider(string reference)
    {
        lock (_gate)
        {
            _fragmentProviders.Remove(reference);
            _components.RemoveAll(item => string.Equals($"{item.Owner}#{item.Id}", reference, StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<(string Reference, IPromptFragmentProvider Provider)> FragmentProviders
    {
        get { lock (_gate) return _fragmentProviders.Select(pair => (pair.Key, pair.Value)).ToArray(); }
    }

    public bool TryGetPrompt(string reference, out IPromptProvider? provider)
    {
        lock (_gate) return _prompts.TryGetValue(reference, out provider);
    }

    public bool TryGetAssembler(string reference, out IContextAssembler? assembler)
    {
        lock (_gate) return _assemblers.TryGetValue(reference, out assembler);
    }

    public bool TryGetMemory(string reference, out IMemoryEntryStore? store)
    {
        lock (_gate) return _memories.TryGetValue(reference, out store);
    }

    public void Unregister(string reference)
    {
        lock (_gate)
        {
            _prompts.Remove(reference);
            _assemblers.Remove(reference);
            _memories.Remove(reference);
            _fragmentProviders.Remove(reference);
            _components.RemoveAll(item => string.Equals($"{item.Owner}#{item.Id}", reference, StringComparison.Ordinal));
        }
    }

    private void AddDescriptor(string reference, string type)
    {
        var separator = reference.IndexOf('#');
        var owner = separator <= 0 ? "host" : reference[..separator];
        var id = separator <= 0 ? reference : reference[(separator + 1)..];
        _components.RemoveAll(item => string.Equals($"{item.Owner}#{item.Id}", reference, StringComparison.Ordinal));
        _components.Add(new ContextComponentDescriptor(owner, owner == "host" ? "host" : "plugin", type, id));
    }
}
