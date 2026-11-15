namespace Natter.Application.Chat;

/// <summary>Host registry for selectable prompt/context/memory components.</summary>
public interface IContextComponentCatalog
{
    void RegisterPrompt(string reference, IPromptProvider provider);

    void RegisterAssembler(string reference, IContextAssembler assembler);

    void RegisterMemory(string reference, IMemoryStore store);

    bool TryGetPrompt(string reference, out IPromptProvider? provider);

    bool TryGetAssembler(string reference, out IContextAssembler? assembler);

    bool TryGetMemory(string reference, out IMemoryStore? store);

    IReadOnlyList<ContextComponentDescriptor> Components { get; }

    void Unregister(string reference);
}

public sealed record ContextComponentDescriptor(string Owner, string Source, string Type, string Id);

public sealed class ContextComponentCatalog : IContextComponentCatalog
{
    private readonly Dictionary<string, IPromptProvider> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IContextAssembler> _assemblers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IMemoryStore> _memories = new(StringComparer.Ordinal);
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

    public void RegisterMemory(string reference, IMemoryStore store)
    {
        lock (_gate)
        {
            _memories[reference] = store;
            AddDescriptor(reference, "memory.store");
        }
    }

    public bool TryGetPrompt(string reference, out IPromptProvider? provider)
    {
        lock (_gate) return _prompts.TryGetValue(reference, out provider);
    }

    public bool TryGetAssembler(string reference, out IContextAssembler? assembler)
    {
        lock (_gate) return _assemblers.TryGetValue(reference, out assembler);
    }

    public bool TryGetMemory(string reference, out IMemoryStore? store)
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
