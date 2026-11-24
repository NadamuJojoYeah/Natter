using Natter.Abstractions.Plugins;
using Natter.Plugin.Abstractions;

namespace Natter.Infrastructure.Plugins.Nps;

/// <summary>One node considered by the dependency graph (NPS or legacy plugin).</summary>
internal sealed record NpsGraphInput(
    string Id,
    string Version,
    string Kind,
    PluginState State,
    IReadOnlyList<PluginDependency>? Dependencies,
    IReadOnlyList<PluginDependency>? OptionalDependencies,
    IReadOnlyList<string>? LoadAfter,
    IReadOnlyList<string>? LoadBefore,
    IReadOnlyList<PluginConflict>? Conflicts,
    NpsPluginCandidate? Candidate)
{
    public SemanticVersion? ParsedVersion
        => SemanticVersion.TryParse(Version, out var version) ? version : null;
}

/// <summary>Graph result consumed by the runtime before assembly loading/configure.</summary>
internal sealed class NpsGraphResult
{
    public required IReadOnlyList<NpsGraphInput> Nodes { get; init; }

    public required IReadOnlyList<string> Order { get; init; }

    public required IReadOnlyDictionary<string, PluginErrorDescriptor> Errors { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> BlockedBy { get; init; }

    public required IReadOnlyList<PluginGraphEdge> Edges { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Deterministic dependency/order graph: required dependencies first, then loadAfter/loadBefore,
/// duplicate id, conflict, missing dependency and cycle diagnostics.
/// </summary>
internal static class NpsPluginGraph
{
    public static NpsGraphResult Build(IEnumerable<NpsGraphInput> inputs, SemanticVersion hostApiVersion)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(hostApiVersion);

        var all = inputs.ToArray();
        var errors = new Dictionary<string, PluginErrorDescriptor>(StringComparer.Ordinal);
        var blockedBy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var edges = new List<PluginGraphEdge>();

        var unique = new Dictionary<string, NpsGraphInput>(StringComparer.Ordinal);
        foreach (var group in all.GroupBy(node => node.Id, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                var detail = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["duplicateCount"] = group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture),
                };

                foreach (var node in group)
                {
                    errors[node.Id] = new PluginErrorDescriptor(
                        "dependency",
                        "duplicate-plugin-id",
                        $"Plugin id '{node.Id}' is declared by multiple packages.",
                        detail);
                }

                continue;
            }

            unique[group.Key] = group.Single();
        }

        foreach (var node in unique.Values)
        {
            if (node.State == PluginState.Disabled)
            {
                errors[node.Id] = new PluginErrorDescriptor(
                    "manifest",
                    "disabled",
                    $"Plugin '{node.Id}' is disabled by host policy.");
                continue;
            }

            if (node.Candidate is { } candidate
                && candidate.Manifest.Platforms is { Count: > 0 } platforms
                && !PluginPlatforms.IsSupported(platforms))
            {
                errors[node.Id] = new PluginErrorDescriptor(
                    "manifest",
                    "platform-unsupported",
                    $"Plugin '{node.Id}' does not support platform '{PluginPlatforms.Current}'.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["platform"] = PluginPlatforms.Current,
                    });
                continue;
            }

            if (node.Candidate is { } candidate3)
            {
                if (!VersionRange.TryParse(candidate3.Manifest.HostApi, out var hostRange))
                {
                    errors[node.Id] = new PluginErrorDescriptor(
                        "manifest",
                        "invalid-host-api",
                        $"hostApi '{candidate3.Manifest.HostApi}' is not a SemVer range.");
                    continue;
                }

                if (!hostRange!.IsSatisfiedBy(hostApiVersion))
                {
                    errors[node.Id] = new PluginErrorDescriptor(
                        "manifest",
                        "host-api-incompatible",
                        $"Plugin '{node.Id}' requires host API '{candidate3.Manifest.HostApi}', host is '{NatterPluginApi.Version}'.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["required"] = candidate3.Manifest.HostApi,
                            ["host"] = NatterPluginApi.Version,
                        });
                }
            }
        }

        // Required/optional dependency edges and missing/version diagnostics.
        foreach (var node in unique.Values.Where(node => !errors.ContainsKey(node.Id)))
        {
            foreach (var dependency in node.Dependencies ?? [])
            {
                if (!unique.TryGetValue(dependency.Id, out var target))
                {
                    errors[node.Id] = new PluginErrorDescriptor(
                        "dependency",
                        "missing-dependency",
                        $"Required plugin '{dependency.Id}' was not found.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["dependency"] = dependency.Id,
                            ["range"] = dependency.Version ?? "*",
                        });
                    break;
                }

                if (!RangeMatches(dependency.Version, target))
                {
                    errors[node.Id] = new PluginErrorDescriptor(
                        "dependency",
                        "dependency-version-mismatch",
                        $"Required plugin '{dependency.Id}' does not satisfy '{dependency.Version}'.",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["dependency"] = dependency.Id,
                            ["range"] = dependency.Version ?? "*",
                            ["actual"] = target.Version,
                        });
                    break;
                }

                edges.Add(new PluginGraphEdge(node.Id, dependency.Id, "dependsOn"));
            }

            if (errors.ContainsKey(node.Id))
                continue;

            foreach (var dependency in node.OptionalDependencies ?? [])
            {
                if (!unique.TryGetValue(dependency.Id, out var target))
                    continue;

                if (!RangeMatches(dependency.Version, target))
                {
                    warnings.Add(
                        $"Plugin '{node.Id}' optional dependency '{dependency.Id}' has version '{target.Version}', expected '{dependency.Version}'.");
                    continue;
                }

                edges.Add(new PluginGraphEdge(node.Id, dependency.Id, "optionalDependsOn"));
            }

            foreach (var targetId in node.LoadAfter ?? [])
            {
                if (unique.ContainsKey(targetId))
                    edges.Add(new PluginGraphEdge(node.Id, targetId, "loadAfter"));
                else
                    warnings.Add($"Plugin '{node.Id}' loadAfter target '{targetId}' was not found.");
            }

            foreach (var targetId in node.LoadBefore ?? [])
            {
                if (unique.ContainsKey(targetId))
                    edges.Add(new PluginGraphEdge(node.Id, targetId, "loadBefore"));
                else
                    warnings.Add($"Plugin '{node.Id}' loadBefore target '{targetId}' was not found.");
            }
        }

        // Conflicts: both sides are failed and reported.
        foreach (var node in unique.Values.Where(node => !errors.ContainsKey(node.Id)))
        {
            foreach (var conflict in node.Conflicts ?? [])
            {
                if (!unique.TryGetValue(conflict.Id, out var target))
                    continue;

                if (!RangeMatches(conflict.Version, target))
                    continue;

                errors[node.Id] = ConflictError(node.Id, target.Id, conflict.Version);
                errors[target.Id] = ConflictError(target.Id, node.Id, conflict.Version);
            }
        }

        // Propagate failed/blocked dependencies.
        var propagate = true;
        while (propagate)
        {
            propagate = false;
            foreach (var node in unique.Values.Where(node => !errors.ContainsKey(node.Id)))
            {
                foreach (var dependency in node.Dependencies ?? [])
                {
                    if (!errors.TryGetValue(dependency.Id, out var dependencyError))
                        continue;

                    var blocked = new List<string> { dependency.Id };
                    var detail = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["dependency"] = dependency.Id,
                        ["cause"] = dependencyError.Code,
                    };

                    errors[node.Id] = new PluginErrorDescriptor(
                        "dependency",
                        "dependency-blocked",
                        $"Required plugin '{dependency.Id}' is not available ({dependencyError.Code}).",
                        detail);
                    blockedBy[node.Id] = blocked;
                    propagate = true;
                    break;
                }
            }
        }

        var active = unique.Values
            .Where(node => !errors.ContainsKey(node.Id))
            .ToArray();
        var activeIds = new HashSet<string>(active.Select(node => node.Id), StringComparer.Ordinal);

        var order = TopologicalOrder(activeIds, edges, out var cycle);
        if (cycle.Count > 0)
        {
            var cycleText = string.Join(" -> ", cycle);
            foreach (var id in cycle)
            {
                errors[id] = new PluginErrorDescriptor(
                    "dependency",
                    "dependency-cycle",
                    $"Plugin ordering contains a cycle: {cycleText}.",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["cycle"] = cycleText });
            }

            // S2-C4: only the cycle members fail. The remaining nodes form an acyclic graph and are
            // still ordered (and loaded) instead of the whole plugin set silently going dark.
            var acyclic = activeIds.Where(id => !cycle.Contains(id, StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
            order = TopologicalOrder(acyclic, edges, out _);

            // Nodes depending on a cycle member are blocked too.
            propagate = true;
            while (propagate)
            {
                propagate = false;
                foreach (var node in unique.Values.Where(node => !errors.ContainsKey(node.Id)))
                {
                    var dependency = (node.Dependencies ?? [])
                        .FirstOrDefault(item => errors.ContainsKey(item.Id));
                    if (dependency is null)
                        continue;

                    errors[node.Id] = new PluginErrorDescriptor(
                        "dependency",
                        "dependency-blocked",
                        $"Required plugin '{dependency.Id}' is part of an invalid dependency graph.",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["dependency"] = dependency.Id });
                    blockedBy[node.Id] = [dependency.Id];
                    propagate = true;
                }
            }
        }

        return new NpsGraphResult
        {
            Nodes = all,
            Order = order,
            Errors = errors,
            BlockedBy = blockedBy,
            Edges = edges,
            Warnings = warnings,
        };
    }

    private static PluginErrorDescriptor ConflictError(string pluginId, string otherId, string? range)
        => new(
            "dependency",
            "plugin-conflict",
            $"Plugin '{pluginId}' conflicts with '{otherId}'.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["other"] = otherId,
                ["range"] = range ?? "*",
            });

    private static bool RangeMatches(string? rangeText, NpsGraphInput target)
    {
        if (string.IsNullOrWhiteSpace(rangeText))
            return true;

        if (!VersionRange.TryParse(rangeText, out var range))
            return false;

        return range!.IsSatisfiedBy(target.ParsedVersion);
    }

    private static IReadOnlyList<string> TopologicalOrder(
        HashSet<string> active,
        IReadOnlyList<PluginGraphEdge> edges,
        out IReadOnlyList<string> cycle)
    {
        var inDegree = active.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var outgoing = active.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (!active.Contains(edge.From) || !active.Contains(edge.To))
                continue;

            // Normalize to "From must load before To".
            var (before, after) = edge.Kind switch
            {
                "dependsOn" or "optionalDependsOn" or "loadAfter" => (edge.To, edge.From),
                "loadBefore" => (edge.From, edge.To),
                _ => (edge.From, edge.To),
            };

            if (!active.Contains(before) || !active.Contains(after))
                continue;

            outgoing[before].Add(after);
            inDegree[after]++;
        }

        var ready = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, degree) in inDegree)
        {
            if (degree == 0)
                ready.Add(id);
        }

        var order = new List<string>(active.Count);
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            order.Add(current);

            foreach (var next in outgoing[current].OrderBy(id => id, StringComparer.Ordinal))
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                    ready.Add(next);
            }
        }

        if (order.Count == active.Count)
        {
            cycle = [];
            return order;
        }

        cycle = FindCycle(active, outgoing, inDegree);
        return [];
    }

    private static IReadOnlyList<string> FindCycle(
        HashSet<string> active,
        IReadOnlyDictionary<string, List<string>> outgoing,
        IReadOnlyDictionary<string, int> inDegree)
    {
        var remaining = active.Where(id => inDegree[id] > 0).ToHashSet(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var start in remaining.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (Visit(start))
                return stack.ToArray();
        }

        return [];

        bool Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);

            IEnumerable<string> nextNodes = outgoing.TryGetValue(node, out var targets)
                ? targets.Where(remaining.Contains).OrderBy(id => id, StringComparer.Ordinal)
                : Enumerable.Empty<string>();

            foreach (var next in nextNodes)
            {
                if (!state.TryGetValue(next, out var nextState))
                {
                    if (Visit(next))
                        return true;
                }
                else if (nextState == 1)
                {
                    var index = stack.IndexOf(next);
                    if (index >= 0)
                    {
                        stack.RemoveRange(0, index);
                        stack.Add(next);
                    }

                    return true;
                }
            }

            state[node] = 2;
            stack.RemoveAt(stack.Count - 1);
            return false;
        }
    }
}

/// <summary>Current platform helpers.</summary>
internal static class PluginPlatforms
{
    public static string Current
    {
        get
        {
            if (OperatingSystem.IsAndroid()) return "android";
            if (OperatingSystem.IsWindows()) return "windows";
            if (OperatingSystem.IsMacOS()) return "macos";
            if (OperatingSystem.IsLinux()) return "linux";
            return "unknown";
        }
    }

    public static bool IsSupported(IReadOnlyList<string> platforms)
        => platforms.Count == 0
           || platforms.Any(platform => string.Equals(platform, Current, StringComparison.OrdinalIgnoreCase));
}
