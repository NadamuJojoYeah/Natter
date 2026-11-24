using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Mechanised obligations for NPS contribution types (docs/CONTRIBUTION_OBLIGATIONS.md).
/// Each test is a ratchet: it fails when an obligation silently regresses, and it pairs a positive
/// case with a negative one so "always passes" cannot be mistaken for "enforced".
/// </summary>
public sealed class ContributionObligationTests
{
    // ---------------------------------------------------------------------------------------------
    // 4.1 Every contribution type must declare its permission policy explicitly
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Every_contribution_type_declares_its_permission_policy()
    {
        // The old switch ended with `_ => []`, so a type nobody listed silently required nothing.
        // That is exactly how 'port.adapter' ended up unchecked for two releases.
        Assert.NotEmpty(PluginContributionTypes.All);

        foreach (var type in PluginContributionTypes.All)
        {
            // Must not throw: a missing arm is a policy hole, not a runtime surprise.
            var required = NpsPermissionPolicy.RequiredPermissions(type);

            Assert.NotNull(required);

            // Permissions must be known permission words, otherwise the grant set can never match.
            Assert.All(required, permission => Assert.True(
                PluginPermissions.IsKnown(permission),
                $"'{type}' requires unknown permission '{permission}'."));
        }
    }

    [Fact]
    public void Unknown_contribution_type_throws_instead_of_defaulting_to_no_permissions()
    {
        // Negative verification for the test above: without this, reintroducing `_ => []`
        // would keep the positive test green while the hole came back.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpsPermissionPolicy.RequiredPermissions("contribution.type.that.does.not.exist"));
    }

    [Fact]
    public void Port_adapter_requirement_is_an_explicit_decision_not_an_omission()
    {
        // Port overrides currently require no permission. That is a recorded decision (bundled
        // samples declare port.adapter with only cli.commands), not a forgotten switch arm — this
        // test exists so the decision cannot be silently reversed or silently "fixed" the wrong way.
        var required = NpsPermissionPolicy.RequiredPermissions(PluginContributionTypes.PortAdapter);

        // Empty on purpose. If this ever becomes non-empty, samples/Natter.Sample.Plugin.Greetings
        // (port.adapter with only cli.commands) must be updated in the same change or it stops
        // activating — see CONTRIBUTION_OBLIGATIONS §5.4. This assertion is the tripwire for that.
        Assert.Empty(required);
    }

    // ---------------------------------------------------------------------------------------------
    // 4.2 chat.upstream chunking obligation has no third state
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Chunking_obligation_has_no_third_state()
    {
        // The obligation is conditional because an aggregation protocol (Satori) genuinely cannot
        // know one upper bound: declare a limit -> must chunk at it; declare null -> may waive, but
        // the waiver must be loud. "Forgot to decide" is the state that used to exist.
        var root = RepoRoot();

        var adapters = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOrArtifact(path))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), @":\s*IChatUpstream\b"))
            .Select(path => (Path: Path.GetRelativePath(root, path), Text: File.ReadAllText(path)))
            .ToArray();

        Assert.NotEmpty(adapters);

        var undecided = new List<string>();

        foreach (var (path, text) in adapters)
        {
            var chunks = text.Contains("ChatTextChunker.Chunk", StringComparison.Ordinal);

            // The waiver branch must be visible where the decision is made: a warning call naming
            // chunking, or a comment that says the limit may be null on purpose.
            var waivesLoudly =
                text.Contains("WarnIfChunkingDisabled", StringComparison.Ordinal)
                || text.Contains("chunking is disabled", StringComparison.OrdinalIgnoreCase);

            if (!chunks && !waivesLoudly)
                undecided.Add(path);
        }

        // Neither branch taken = the Satori bug: an adapter that believes it is done.
        Assert.Empty(undecided);
    }

    [Fact]
    public void Chunking_receives_the_declared_limit_rather_than_a_hard_coded_number()
    {
        // A hard-coded limit in an adapter would drift from the capability it advertises, so the
        // chunk call must be fed by the capability/options value.
        var root = RepoRoot();

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOrArtifact(path))
            .Where(path => File.ReadAllText(path).Contains("ChatTextChunker.Chunk", StringComparison.Ordinal))
            .Select(path => (Path: Path.GetRelativePath(root, path), Text: File.ReadAllText(path)))
            .Where(file => !file.Text.Contains("MaxMessageLength", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Shipping code only. Test doubles implement IChatUpstream on purpose to prove the host's
    /// behaviour (including that the host does not chunk for them), so they are not subject to an
    /// adapter obligation.
    /// </summary>
    private static bool IsBuildOrArtifact(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
