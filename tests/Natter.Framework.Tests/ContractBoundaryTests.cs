using Natter.Application.Chat;
using Natter.Application.Redaction;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>B-02a: lock the assembly boundary that moved plugin-visible contracts out of Application.</summary>
public sealed class ContractBoundaryTests
{
    [Fact]
    public void Redaction_and_embedding_contracts_live_in_abstractions()
    {
        Assert.Equal("Natter.Abstractions", typeof(SecretRedaction).Assembly.GetName().Name);
        Assert.Equal("Natter.Abstractions", typeof(RedactedText).Assembly.GetName().Name);
        Assert.Equal("Natter.Abstractions", typeof(EmbeddingRequest).Assembly.GetName().Name);
        Assert.Equal("Natter.Abstractions", typeof(IEmbeddingProvider).Assembly.GetName().Name);
    }

    [Fact]
    public void AiEmbedding_and_discord_do_not_reference_application()
    {
        AssertNoProjectReference("plugins/Natter.Plugin.AiEmbedding.OpenAi/Natter.Plugin.AiEmbedding.OpenAi.csproj", "Natter.Application");
        AssertNoProjectReference("src/Natter.Chat.Discord/Natter.Chat.Discord.csproj", "Natter.Application");
    }

    [Fact]
    public void DingTalk_references_abstractions_for_shared_redaction()
    {
        AssertProjectReference("src/Natter.Chat.DingTalk/Natter.Chat.DingTalk.csproj", "Natter.Abstractions");
    }

    [Fact]
    public void PluginAbstractions_does_not_reference_application()
    {
        AssertNoProjectReference("src/Natter.Plugin.Abstractions/Natter.Plugin.Abstractions.csproj", "Natter.Application");
        AssertProjectReference("src/Natter.Plugin.Abstractions/Natter.Plugin.Abstractions.csproj", "Natter.Contracts");
    }

    [Fact]
    public void PromptLibrary_and_sample_memory_use_contract_projects_not_application()
    {
        AssertNoProjectReference("plugins/Natter.Plugin.PromptLibrary/Natter.Plugin.PromptLibrary.csproj", "Natter.Application");
        AssertProjectReference("plugins/Natter.Plugin.PromptLibrary/Natter.Plugin.PromptLibrary.csproj", "Natter.Prompting");
        AssertNoProjectReference("samples/Natter.Sample.Plugin.Memory/Natter.Sample.Plugin.Memory.csproj", "Natter.Application");
        AssertNoProjectReference("samples/Natter.Sample.Plugin.Memory/Natter.Sample.Plugin.Memory.csproj", "Natter.Chat");
    }

    [Fact]
    public void Contract_assemblies_are_wired_without_a_cycle()
    {
        Assert.Equal("Natter.Contracts", typeof(PromptRequest).Assembly.GetName().Name);
        Assert.Equal("Natter.Prompting", typeof(PromptComposer).Assembly.GetName().Name);
    }

    private static void AssertProjectReference(string relativePath, string name)
        => Assert.Contains(
            $"Natter.{name.Split('.').Last()}.csproj",
            ReadProject(relativePath),
            StringComparison.Ordinal);

    private static void AssertNoProjectReference(string relativePath, string name)
        => Assert.DoesNotContain(
            $"Natter.{name.Split('.').Last()}.csproj",
            ReadProject(relativePath),
            StringComparison.Ordinal);

    private static string ReadProject(string relativePath)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Natter.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, relativePath));
    }
}
