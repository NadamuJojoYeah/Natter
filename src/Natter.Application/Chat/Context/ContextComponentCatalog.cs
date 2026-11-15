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
}

public sealed class ContextComponentCatalog : IContextComponentCatalog
{
    private readonly Dictionary<string, IPromptProvider> _prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IContextAssembler> _assemblers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IMemoryStore> _memories = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void RegisterPrompt(string reference, IPromptProvider provider)
    {
        lock (_gate) _prompts[reference] = provider;
    }

    public void RegisterAssembler(string reference, IContextAssembler assembler)
    {
        lock (_gate) _assemblers[reference] = assembler;
    }

    public void RegisterMemory(string reference, IMemoryStore store)
    {
        lock (_gate) _memories[reference] = store;
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
}
