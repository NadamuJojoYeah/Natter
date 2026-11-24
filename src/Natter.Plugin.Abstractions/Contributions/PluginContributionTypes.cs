namespace Natter.Plugin.Abstractions;

/// <summary>NPS v1 contribution type names.</summary>
public static class PluginContributionTypes
{
    public const string ChatUpstream = "chat.upstream";
    public const string ChatResponder = "chat.responder";
    public const string ChatEventSubscriber = "chat.event-subscriber";
    public const string CliCommand = "cli.command";
    public const string PortAdapter = "port.adapter";
    public const string PromptProvider = "prompt.provider";
    public const string PromptFragment = "prompt.fragment";
    public const string ContextAssembler = "context.assembler";
    public const string MemoryStore = "memory.store";
    public const string EmbeddingProvider = "embedding.provider";
    public const string RerankProvider = "rerank.provider";

    public static IReadOnlyList<string> All { get; } =
        [ChatUpstream, ChatResponder, ChatEventSubscriber, CliCommand, PortAdapter,
         PromptProvider, PromptFragment, ContextAssembler, MemoryStore,
         EmbeddingProvider, RerankProvider];

    public static bool IsKnown(string? type)
        => type is not null && All.Contains(type, StringComparer.Ordinal);
}
