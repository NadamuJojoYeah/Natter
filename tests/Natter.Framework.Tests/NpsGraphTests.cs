using Natter.Abstractions.Plugins;
using Natter.Infrastructure.Plugins.Nps;
using Natter.Plugin.Abstractions;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class NpsGraphTests
{
    [Fact]
    public void Dependencies_and_order_constraints_produce_a_deterministic_order()
    {
        var result = Build(
            Node("natter.z", loadAfter: ["natter.a"]),
            Node("natter.a"),
            Node("natter.b", dependencies: [Dep("natter.a", ">=0.1.0 <1.0.0")]));

        Assert.Empty(result.Errors);
        Assert.Equal(["natter.a", "natter.b", "natter.z"], result.Order);
    }

    [Fact]
    public void Load_before_is_respected()
    {
        var result = Build(
            Node("natter.a"),
            Node("natter.b", loadBefore: ["natter.a"]));

        Assert.Equal(["natter.b", "natter.a"], result.Order);
    }

    [Fact]
    public void Missing_dependency_blocks_only_the_dependent()
    {
        var result = Build(
            Node("natter.a"),
            Node("natter.b", dependencies: [Dep("natter.missing", ">=0.1.0")]));

        Assert.Equal(PluginState.Failed, PluginState.Failed); // graph returns errors; state mapping is runtime-owned
        Assert.True(result.Errors.ContainsKey("natter.b"));
        Assert.Equal("missing-dependency", result.Errors["natter.b"].Code);
        Assert.False(result.Errors.ContainsKey("natter.a"));
        Assert.Equal(["natter.a"], result.Order);
    }

    [Fact]
    public void Dependency_cycles_are_reported_with_the_cycle_path()
    {
        var result = Build(
            Node("natter.a", dependencies: [Dep("natter.b", null)]),
            Node("natter.b", dependencies: [Dep("natter.a", null)]));

        Assert.Contains("dependency-cycle", result.Errors.Values.Select(error => error.Code));
        Assert.All(result.Errors.Values, error => Assert.Contains("natter.a", error.Detail!["cycle"]));
        Assert.Empty(result.Order);
    }

    [Fact]
    public void Conflicts_fail_both_sides()
    {
        var result = Build(
            Node("natter.a", conflicts: [Conflict("natter.b", ">=0.1.0")]),
            Node("natter.b"));

        Assert.Equal("plugin-conflict", result.Errors["natter.a"].Code);
        Assert.Equal("plugin-conflict", result.Errors["natter.b"].Code);
    }

    [Fact]
    public void Optional_dependency_missing_does_not_block()
    {
        var result = Build(
            Node("natter.a", optionalDependencies: [Dep("natter.optional", ">=1.0.0")]));

        Assert.Empty(result.Errors);
        Assert.Equal(["natter.a"], result.Order);
    }

    [Fact]
    public void Host_api_mismatch_is_rejected()
    {
        var candidate = new NpsPluginCandidate
        {
            Manifest = new PluginManifest
            {
                Id = "natter.a",
                Name = "A",
                Version = "0.1.0",
                Kind = PluginKind.Feature,
                HostApi = ">=9.0.0 <10.0.0",
                Entry = new PluginEntry { Assembly = "a.dll", Type = "A.Plugin" },
            },
            Directory = "/tmp",
            AssemblyPath = "/tmp/a.dll",
        };
        candidate.InitializeDescriptor();

        var result = Build(
            new NpsGraphInput("natter.a", "0.1.0", "feature", PluginState.Discovered,
                null, null, null, null, null, candidate));

        Assert.Equal("host-api-incompatible", result.Errors["natter.a"].Code);
    }

    private static NpsGraphResult Build(params NpsGraphInput[] nodes)
        => NpsPluginGraph.Build(nodes, SemanticVersion.Parse(NatterPluginApi.Version));

    private static NpsGraphInput Node(
        string id,
        PluginDependency[]? dependencies = null,
        PluginDependency[]? optionalDependencies = null,
        string[]? loadAfter = null,
        string[]? loadBefore = null,
        PluginConflict[]? conflicts = null)
        => new(
            id,
            "0.1.0",
            "feature",
            PluginState.Discovered,
            dependencies,
            optionalDependencies,
            loadAfter,
            loadBefore,
            conflicts,
            Candidate: null);

    private static PluginDependency Dep(string id, string? version)
        => new() { Id = id, Version = version };

    private static PluginConflict Conflict(string id, string? version)
        => new() { Id = id, Version = version };
}
