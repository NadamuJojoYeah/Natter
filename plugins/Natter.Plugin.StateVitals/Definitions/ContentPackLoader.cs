using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Natter.Plugin.StateVitals.Definitions;

/// <summary>
/// Loads <c>&lt;config&gt;/state-vitals/packs/*.json</c> into one resolved content set
/// (design §9.3 / §10.1).
///
/// <para>
/// The five primitives are frozen: a pack can only instantiate <c>vital</c>,
/// <c>assertedState</c>, <c>derivedState</c>, <c>effect</c> and <c>trigger</c>. Inheritance is one
/// deep JSON merge (<c>abstract</c> + <c>parent</c>), arrays are replaced wholesale, and
/// <c>mayRequire</c> makes a definition or a whole pack quietly disappear when a soft dependency is
/// not installed — visible in the validate output, never a startup failure.
/// </para>
/// </summary>
public static class ContentPackLoader
{
    public const double MaxRatePerHour = 2d;
    public const double MaxBiasPerHour = 1000d;
    public const double MaxRateMultiplier = 10d;
    public const int MaxPeriodHours = 24 * 400;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads a pack directory; any problem becomes a startup exception with all issues.</summary>
    public static ContentPackSet Load(
        string packsDirectory,
        VitalRuleRegistry? rules = null,
        PackLoadOptions? options = null)
    {
        var result = TryLoad(packsDirectory, rules, options);
        if (result.Set is null || result.Issues.Count > 0)
        {
            var code = result.Issues.Count > 0 ? result.Issues[0].Code : StateVitalsCodes.PackDirectoryMissing;
            throw new StateVitalsStartupException(code, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString())));
        }

        return result.Set;
    }

    /// <summary>Tolerant load used by <c>states validate</c> and by tests.</summary>
    public static PackLoadResult TryLoad(
        string packsDirectory,
        VitalRuleRegistry? rules = null,
        PackLoadOptions? options = null)
    {
        options ??= PackLoadOptions.Default;
        var issues = new List<PackIssue>();
        var skipped = new List<SkippedDefinition>();

        if (string.IsNullOrWhiteSpace(packsDirectory) || !Directory.Exists(packsDirectory))
        {
            issues.Add(new PackIssue(
                packsDirectory ?? "(null)",
                string.Empty,
                StateVitalsCodes.PackDirectoryMissing,
                "内容包目录不存在。",
                "创建该目录并放入至少一个包文件，或修正配置里的 packs 路径。"));
            return new PackLoadResult(null, issues, skipped);
        }

        var files = Directory.GetFiles(packsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            issues.Add(new PackIssue(
                packsDirectory,
                string.Empty,
                StateVitalsCodes.PackDirectoryMissing,
                "内容包目录里没有任何 .json 文件。",
                "至少提供一个包文件，例如 core.json。"));
            return new PackLoadResult(null, issues, skipped);
        }

        if (files.Length > options.MaxPacks)
        {
            issues.Add(new PackIssue(
                packsDirectory,
                string.Empty,
                StateVitalsCodes.PackLimit,
                $"包数量 {files.Length} 超过上限 {options.MaxPacks}。",
                "合并或禁用一部分包。"));
            return new PackLoadResult(null, issues, skipped);
        }

        var parsed = new List<ParsedPack>();
        foreach (var file in files)
        {
            var pack = ParsePack(file, issues);
            if (pack is not null)
                parsed.Add(pack);
        }

        if (issues.Count > 0)
            return new PackLoadResult(null, issues, skipped);

        var duplicatedPack = parsed.GroupBy(pack => pack.PackId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicatedPack is not null)
        {
            foreach (var pack in duplicatedPack)
            {
                issues.Add(new PackIssue(
                    pack.SourcePath,
                    "/packId",
                    StateVitalsCodes.PackDuplicate,
                    $"packId '{pack.PackId}' 被多个文件声明。",
                    "让每个包文件使用唯一的 packId。"));
            }

            return new PackLoadResult(null, issues, skipped);
        }

        var enabledPacks = parsed.Where(pack => pack.Enabled).ToArray();
        var enabledIds = enabledPacks.Select(pack => pack.PackId).ToHashSet(StringComparer.Ordinal);

        foreach (var pack in parsed.Where(pack => !pack.Enabled))
            skipped.Add(new SkippedDefinition(pack.PackId, "*", "pack disabled"));

        // Soft dependency: a pack whose mayRequire is not installed simply does not exist (§9.5).
        var activePacks = new List<ParsedPack>();
        foreach (var pack in enabledPacks)
        {
            var missing = pack.MayRequire.Where(required => !enabledIds.Contains(required)).ToArray();
            if (missing.Length > 0)
            {
                skipped.Add(new SkippedDefinition(pack.PackId, "*", $"mayRequire not installed: {string.Join(", ", missing)}"));
                continue;
            }

            activePacks.Add(pack);
        }

        var activeIds = activePacks.Select(pack => pack.PackId).ToHashSet(StringComparer.Ordinal);
        var packs = new List<ContentPack>();
        var allVitals = new List<VitalDefinition>();
        var allAsserted = new List<AssertedStateDefinition>();
        var allDerived = new List<DerivedStateDefinition>();
        var allEffects = new List<StateEffect>();
        var allTriggers = new List<TriggerDefinition>();
        var declaredRules = new List<string>();

        foreach (var pack in activePacks)
        {
            var resolved = ResolvePack(pack, activeIds, rules, options, issues, skipped);
            if (resolved is null)
                continue;

            packs.Add(resolved);
            allVitals.AddRange(resolved.Vitals);
            allAsserted.AddRange(resolved.AssertedStates);
            allDerived.AddRange(resolved.DerivedStates);
            allEffects.AddRange(resolved.Effects);
            allTriggers.AddRange(resolved.Triggers);

            foreach (var rule in pack.Rules)
            {
                if (!declaredRules.Contains(rule, StringComparer.Ordinal))
                    declaredRules.Add(rule);
            }
        }

        if (issues.Count > 0)
            return new PackLoadResult(null, issues, skipped);

        ValidateCrossReferences(allVitals, allAsserted, allDerived, allEffects, allTriggers, issues, packs);
        ValidateDuplicateIds(allVitals, allAsserted, allDerived, issues);

        if (issues.Count > 0)
            return new PackLoadResult(null, issues, skipped);

        var definitionVersion = DefinitionVersionOf(packs);
        var set = new ContentPackSet(
            1,
            definitionVersion,
            packs,
            allVitals,
            allAsserted,
            allDerived,
            allEffects,
            allTriggers,
            declaredRules,
            skipped);

        return new PackLoadResult(set, issues, skipped);
    }

    /// <summary>Deterministic identity of the content set; persisted with every snapshot (§8.2).</summary>
    public static string DefinitionVersionOf(IReadOnlyList<ContentPack> packs)
    {
        var builder = new StringBuilder("packs1");
        foreach (var pack in packs.OrderBy(pack => pack.PackId, StringComparer.Ordinal))
            builder.Append(' ').Append(pack.PackId).Append('@').Append(pack.Version);

        return builder.ToString();
    }

    // ------------------------------------------------------------------ pack parsing

    private sealed record ParsedPack(
        string PackId,
        string Version,
        bool Enabled,
        IReadOnlyList<string> MayRequire,
        DescriptionVoice Voice,
        JsonElement Root,
        IReadOnlyList<string> Rules,
        string SourcePath,
        string DisplaySource);

    private static ParsedPack? ParsePack(string file, List<PackIssue> issues)
    {
        var display = Path.GetFileName(file);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(file), DocumentOptions);
        }
        catch (JsonException exception)
        {
            issues.Add(new PackIssue(display, string.Empty, StateVitalsCodes.DefinitionInvalidJson, $"不是合法 JSON：{exception.Message}", "修正 JSON 语法。"));
            return null;
        }
        catch (IOException exception)
        {
            issues.Add(new PackIssue(display, string.Empty, StateVitalsCodes.DefinitionUnreadable, exception.Message, "检查文件权限。"));
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new PackIssue(display, string.Empty, StateVitalsCodes.DefinitionInvalidJson, "包根节点必须是对象。", "用 { } 包裹定义。"));
                return null;
            }

            var before = issues.Count;

            if (!root.TryGetProperty("schemaVersion", out var schemaElement) || schemaElement.ValueKind != JsonValueKind.Number)
            {
                issues.Add(new PackIssue(display, "/schemaVersion", StateVitalsCodes.DefinitionSchemaVersionMissing, "缺少 schemaVersion。", "加 \"schemaVersion\": 1。"));
            }
            else if (!ContentPackSet.SupportedSchemaVersions.Contains(schemaElement.GetInt32()))
            {
                issues.Add(new PackIssue(
                    display,
                    "/schemaVersion",
                    StateVitalsCodes.DefinitionSchemaVersionUnsupported,
                    $"schemaVersion {schemaElement.GetInt32()} 不受支持。",
                    $"改为 {string.Join(" 或 ", ContentPackSet.SupportedSchemaVersions)}。"));
            }

            var packId = string.Empty;
            if (!root.TryGetProperty("packId", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !IsLocalId(idElement.GetString()))
            {
                issues.Add(new PackIssue(
                    display,
                    "/packId",
                    StateVitalsCodes.PackIdInvalid,
                    "packId 缺失或不是合法局部 id（字母/数字/._-，最长 64）。",
                    "例如 \"packId\": \"core\"。"));
            }
            else
            {
                packId = idElement.GetString()!;
            }

            var version = "0.0.0";
            if (!root.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(versionElement.GetString()))
            {
                issues.Add(new PackIssue(display, "/version", StateVitalsCodes.PackVersionMissing, "缺少 version。", "加 \"version\": \"1.0.0\"（会随快照落库）。"));
            }
            else
            {
                version = versionElement.GetString()!.Trim();
            }

            var enabled = true;
            if (root.TryGetProperty("enabled", out var enabledElement))
            {
                if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    issues.Add(new PackIssue(display, "/enabled", StateVitalsCodes.DefinitionInvalidJson, "enabled 必须是布尔值。", "改为 true 或 false。"));
                else
                    enabled = enabledElement.GetBoolean();
            }

            var mayRequire = ReadStringArray(root, "mayRequire", display, issues);
            var rules = ReadStringArray(root, "rules", display, issues);
            var voice = ParseVoice(root, display, issues);

            return issues.Count > before
                ? null
                : new ParsedPack(packId, version, enabled, mayRequire, voice, root.Clone(), rules, file, display);
        }
    }

    private static ContentPack? ResolvePack(
        ParsedPack pack,
        IReadOnlySet<string> activePackIds,
        VitalRuleRegistry? rules,
        PackLoadOptions options,
        List<PackIssue> issues,
        List<SkippedDefinition> skipped)
    {
        var before = issues.Count;
        var root = pack.Root;

        var vitalElements = ResolveKind(root, pack, "vitals", activePackIds, options.MaxVitalsPerPack, issues, skipped);
        var assertedElements = ResolveKind(root, pack, "assertedStates", activePackIds, options.MaxAssertedStatesPerPack, issues, skipped);
        var derivedElements = ResolveKind(root, pack, "derivedStates", activePackIds, options.MaxDerivedStatesPerPack, issues, skipped);
        var effectElements = ResolveKind(root, pack, "effects", activePackIds, options.MaxEffectsPerPack, issues, skipped);
        var triggerElements = ResolveKind(root, pack, "triggers", activePackIds, options.MaxTriggersPerPack, issues, skipped);

        foreach (var rule in pack.Rules)
        {
            // Unregistered rule names are a startup error, never a silent runtime skip (§10.1).
            if (rules is null || !rules.Contains(rule))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    "/rules",
                    StateVitalsCodes.RuleUnknown,
                    $"rule '{rule}' 未注册。",
                    "删掉该 rule，或让宿主注册同名的 IVitalRule 实现。"));
            }
        }

        var vitals = new List<VitalDefinition>();
        foreach (var (element, pointer) in vitalElements)
            AddIfParsed(vitals, ParseVital(element, pack, pointer, issues));

        var asserted = new List<AssertedStateDefinition>();
        foreach (var (element, pointer) in assertedElements)
            AddIfParsed(asserted, ParseAsserted(element, pack, pointer, issues));

        var derived = new List<DerivedStateDefinition>();
        foreach (var (element, pointer) in derivedElements)
            AddIfParsed(derived, ParseDerived(element, pack, pointer, issues));

        var effects = new List<StateEffect>();
        foreach (var (element, pointer) in effectElements)
            AddIfParsed(effects, ParseEffect(element, pack, pointer, issues));

        var triggers = new List<TriggerDefinition>();
        foreach (var (element, pointer) in triggerElements)
            AddIfParsed(triggers, ParseTrigger(element, pack, pointer, issues));

        if (issues.Count > before)
            return null;

        return new ContentPack(
            pack.PackId,
            pack.Version,
            pack.Enabled,
            pack.MayRequire,
            pack.Voice,
            vitals,
            asserted,
            derived,
            effects,
            triggers,
            pack.DisplaySource);
    }

    private static void AddIfParsed<T>(List<T> target, T? value) where T : class
    {
        if (value is not null)
            target.Add(value);
    }

    /// <summary>
    /// Resolves one definition array: applies <c>abstract</c>/<c>parent</c> inheritance, honours
    /// <c>mayRequire</c>, enforces the per-pack quota and returns merged JSON with pointers.
    /// </summary>
    private static IReadOnlyList<(JsonNode Node, string Pointer)> ResolveKind(
        JsonElement root,
        ParsedPack pack,
        string property,
        IReadOnlySet<string> activePackIds,
        int quota,
        List<PackIssue> issues,
        List<SkippedDefinition> skipped)
    {
        var result = new List<(JsonNode, string)>();
        if (!root.TryGetProperty(property, out var array))
            return result;

        if (array.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PackIssue(pack.DisplaySource, $"/{property}", StateVitalsCodes.DefinitionInvalidJson, $"{property} 必须是数组。", "改为 [ ... ]。"));
            return result;
        }

        var raw = new List<(JsonObject Node, string Pointer)>();
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var pointer = $"/{property}/{index}";
            index++;

            if (element.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new PackIssue(pack.DisplaySource, pointer, StateVitalsCodes.DefinitionInvalidJson, "定义必须是对象。", "改为 { ... }。"));
                continue;
            }

            var node = JsonNode.Parse(element.GetRawText()) as JsonObject;
            if (node is null)
            {
                issues.Add(new PackIssue(pack.DisplaySource, pointer, StateVitalsCodes.DefinitionInvalidJson, "定义无法解析。", "检查 JSON 结构。"));
                continue;
            }

            // Definition-level soft dependency: silently skip, but say so in validate output.
            var mayRequire = ReadSingleString(node, "mayRequire");
            if (!string.IsNullOrWhiteSpace(mayRequire)
                && !activePackIds.Contains(PackScopedId.PackPart(PackScopedId.Resolve(pack.PackId, mayRequire))))
            {
                var localId = ReadSingleString(node, "id") ?? "?";
                skipped.Add(new SkippedDefinition(pack.PackId, PackScopedId.Resolve(pack.PackId, localId), $"mayRequire not installed: {mayRequire}"));
                continue;
            }

            raw.Add((node, pointer));
        }

        // Build id -> (node, pointer, parent) then resolve parent chains with cycle detection.
        var byId = new Dictionary<string, (JsonObject Node, string Pointer)>(StringComparer.Ordinal);
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (node, pointer) in raw)
        {
            var rawId = ReadSingleString(node, "id");
            if (string.IsNullOrWhiteSpace(rawId) || !IsLocalId(rawId))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/id",
                    StateVitalsCodes.DefinitionIdInvalid,
                    "id 缺失或不是合法局部 id。",
                    "用 id 表示局部名（例如 \"hunger\"），不要写 pack 前缀，也不要包含 #。"));
                continue;
            }

            if (!byId.TryAdd(rawId, (node, pointer)))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/id",
                    StateVitalsCodes.DefinitionDuplicate,
                    $"id '{rawId}' 在 {property} 中重复。",
                    "删除重复定义或改用不同 id。"));
                continue;
            }

            order.Add(rawId);
            var parent = ReadSingleString(node, "parent");
            if (!string.IsNullOrWhiteSpace(parent))
                parents[rawId] = parent;
        }

        foreach (var id in order)
        {
            var (node, pointer) = byId[id];
            if (JsonBool(node, "abstract") == true)
                continue;

            var merged = MergeChain(id, byId, parents, pack, property, issues);
            if (merged is null)
                continue;

            result.Add((merged, pointer));
        }

        if (result.Count > quota)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                $"/{property}",
                StateVitalsCodes.DefinitionQuota,
                $"包 '{pack.PackId}' 的 {property} 数量 {result.Count} 超过每包上限 {quota}。",
                "拆分到另一个包，或减少定义数量。"));
            return [];
        }

        return result;
    }

    private static JsonNode? MergeChain(
        string id,
        Dictionary<string, (JsonObject Node, string Pointer)> byId,
        Dictionary<string, string> parents,
        ParsedPack pack,
        string property,
        List<PackIssue> issues)
    {
        // Build the lineage child → root (inclusive), then merge root → child so the child wins.
        var lineage = new List<string> { id };
        var visited = new HashSet<string>(StringComparer.Ordinal) { id };
        var current = id;

        while (parents.TryGetValue(current, out var parent))
        {
            var resolvedParent = PackScopedId.Resolve(pack.PackId, parent);
            if (PackScopedId.PackPart(resolvedParent) != pack.PackId
                || !byId.ContainsKey(PackScopedId.LocalPart(resolvedParent)))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    byId[current].Pointer + "/parent",
                    StateVitalsCodes.ParentMissing,
                    $"parent '{parent}' 在 {property} 中不存在。",
                    "补上该 abstract 定义，或改成已存在的 id。"));
                return null;
            }

            var localParent = PackScopedId.LocalPart(resolvedParent);
            if (!visited.Add(localParent))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    byId[id].Pointer + "/parent",
                    StateVitalsCodes.ParentCycle,
                    $"parent 链存在环：{string.Join(" → ", lineage.Append(localParent))}。",
                    "断开环，让每个定义的 parent 最终指向一个 abstract 定义。"));
                return null;
            }

            lineage.Add(localParent);
            current = localParent;
        }

        JsonObject merged = new();
        for (var index = lineage.Count - 1; index >= 0; index--)
            merged = DeepMerge(merged, byId[lineage[index]].Node);

        merged.Remove("abstract");
        merged.Remove("parent");
        return merged;
    }

    /// <summary>One deep merge (design §9.4): objects merge, arrays and scalars are replaced.</summary>
    private static JsonObject DeepMerge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child)
        {
            if (value is JsonObject childObject && result[key] is JsonObject parentObject)
                result[key] = DeepMerge(parentObject, childObject);
            else
                result[key] = value?.DeepClone();
        }

        return result;
    }

    // ------------------------------------------------------------------ definitions

    private static VitalDefinition? ParseVital(JsonNode node, ParsedPack pack, string pointer, List<PackIssue> issues)
    {
        var before = issues.Count;
        var localId = ReadSingleString(node, "id")!;
        var id = PackScopedId.Scoped(pack.PackId, localId);
        var label = ReadSingleString(node, "label") ?? localId;

        if (!TryReadRange(node, pack, pointer, localId, issues, out var range) || range is null)
            return null;

        var baseline = range.Min;
        if (node["baseline"] is JsonValue baselineValue && baselineValue.TryGetValue<double>(out var parsed))
        {
            baseline = parsed;
            if (baseline < range.Min || baseline > range.Max)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/baseline",
                    StateVitalsCodes.DefinitionBaselineOutOfRange,
                    $"baseline {Format(baseline)} 不在 [{Format(range.Min)}, {Format(range.Max)}] 内。",
                    $"把 baseline 落在范围内，例如 {Format(range.Min)}。"));
            }
        }

        var decay = ParseDecay(node, pack, pointer, localId, range, issues);
        if (decay is null)
            return null;

        var levels = ParseLevels(node, pack, pointer, localId, range, issues);
        if (levels is null)
            return null;

        return issues.Count > before
            ? null
            : new VitalDefinition(id, pack.PackId, label, range, baseline, decay, levels, false, null);
    }

    private static AssertedStateDefinition? ParseAsserted(JsonNode node, ParsedPack pack, string pointer, List<PackIssue> issues)
    {
        var localId = ReadSingleString(node, "id")!;
        var id = PackScopedId.Scoped(pack.PackId, localId);

        if (!TryParseDuration(node, pack, pointer, "durationHours", issues, out var duration))
            return null;

        return new AssertedStateDefinition(
            id,
            pack.PackId,
            ReadSingleString(node, "label") ?? localId,
            ReadSingleString(node, "semantic"),
            ReadSingleString(node, "template"),
            duration,
            false,
            null,
            ReadSingleString(node, "mayRequire"));
    }

    private static DerivedStateDefinition? ParseDerived(JsonNode node, ParsedPack pack, string pointer, List<PackIssue> issues)
    {
        var before = issues.Count;
        var localId = ReadSingleString(node, "id")!;
        var id = PackScopedId.Scoped(pack.PackId, localId);

        var vitalRaw = ReadSingleString(node, "vital");
        if (string.IsNullOrWhiteSpace(vitalRaw))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/vital",
                StateVitalsCodes.DerivedVitalMissing,
                "derivedState 必须声明它读取哪个 vital。",
                "加 \"vital\": \"core#hunger\"（或本包内的局部 id）。"));
        }

        var levels = new List<string>();
        if (node["levels"] is JsonArray levelsArray)
        {
            foreach (var item in levelsArray)
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var name))
                    levels.Add(name);
            }
        }
        else if (ReadSingleString(node, "level") is { Length: > 0 } single)
        {
            levels.Add(single);
        }

        if (levels.Count == 0)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/levels",
                StateVitalsCodes.DerivedLevelsMissing,
                "derivedState 必须声明触发它的档位（level 或 levels）。",
                "加 \"levels\": [\"starving\"]。"));
        }

        if (issues.Count > before)
            return null;

        return new DerivedStateDefinition(
            id,
            pack.PackId,
            ReadSingleString(node, "label") ?? localId,
            PackScopedId.Resolve(pack.PackId, vitalRaw!),
            levels,
            ReadSingleString(node, "semantic"),
            ReadSingleString(node, "template"));
    }

    private static StateEffect? ParseEffect(JsonNode node, ParsedPack pack, string pointer, List<PackIssue> issues)
    {
        var before = issues.Count;
        var localId = ReadSingleString(node, "id") ?? "?";
        var id = PackScopedId.Scoped(pack.PackId, localId.Replace("#", "_", StringComparison.Ordinal));

        var fromRaw = ReadSingleString(node, "from");
        var toRaw = ReadSingleString(node, "to");
        if (string.IsNullOrWhiteSpace(fromRaw))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/from",
                StateVitalsCodes.EffectSourceUnknown,
                "effect 必须声明来源 state。",
                "加 \"from\": \"core#starving\"：effect 只能从 state 指向 vital。"));
        }

        if (string.IsNullOrWhiteSpace(toRaw))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/to",
                StateVitalsCodes.EffectTargetUnknown,
                "effect 必须声明目标 vital。",
                "加 \"to\": \"core#energy\"。"));
        }

        if (!TryReadStacking(node, pack, pointer, localId, issues, out var rule))
            return null;

        var bias = 0d;
        var multiplier = 0d;
        var hasBias = node["bias"] is JsonValue biasValue && biasValue.TryGetValue<double>(out bias);
        var hasMultiplier = node["rateMultiplier"] is JsonValue multiplierValue
                            && multiplierValue.TryGetValue<double>(out multiplier);

        if (hasBias == hasMultiplier)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer,
                StateVitalsCodes.DefinitionEffectKindInvalid,
                "effect 需要 bias 或 rateMultiplier 之一（且只能一个）。",
                "加 \"bias\": -6 表示每小时 -6，或 \"rateMultiplier\": 1.4 表示速率乘 1.4。"));
            return null;
        }

        VitalEffectKind kind;
        double amount;
        if (hasMultiplier)
        {
            if (rule != VitalApplyRule.Mul)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/stacking",
                    StateVitalsCodes.DefinitionEffectStackingIncompatible,
                    "rateMultiplier 只能用 stacking 'mul'。",
                    "改为 \"stacking\": \"mul\"。"));
                return null;
            }

            if (multiplier <= 0 || multiplier > MaxRateMultiplier)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/rateMultiplier",
                    StateVitalsCodes.DefinitionEffectKindInvalid,
                    $"rateMultiplier {Format(multiplier)} 越界。",
                    $"取 (0, {Format(MaxRateMultiplier)}] 之间的值。"));
                return null;
            }

            kind = VitalEffectKind.RateMultiplier;
            amount = multiplier;
        }
        else
        {
            if (rule == VitalApplyRule.Mul)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/stacking",
                    StateVitalsCodes.DefinitionEffectStackingIncompatible,
                    "bias 不能用 stacking 'mul'。",
                    "改为 \"stacking\": \"add\" 或 \"max\"。"));
                return null;
            }

            if (Math.Abs(bias) > MaxBiasPerHour)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/bias",
                    StateVitalsCodes.DefinitionEffectKindInvalid,
                    $"|bias| {Format(Math.Abs(bias))} 超过上限 {Format(MaxBiasPerHour)}。",
                    "缩小每小时变化量。"));
                return null;
            }

            kind = VitalEffectKind.Bias;
            amount = bias;
        }

        var priority = 0;
        if (node["priority"] is JsonValue priorityValue && priorityValue.TryGetValue<int>(out var parsedPriority))
            priority = Math.Clamp(parsedPriority, -1000, 1000);

        if (issues.Count > before)
            return null;

        return new StateEffect(
            id,
            pack.PackId,
            PackScopedId.Resolve(pack.PackId, fromRaw!),
            PackScopedId.Resolve(pack.PackId, toRaw!),
            kind,
            rule,
            amount,
            priority);
    }

    private static TriggerDefinition? ParseTrigger(JsonNode node, ParsedPack pack, string pointer, List<PackIssue> issues)
    {
        var before = issues.Count;
        var localId = ReadSingleString(node, "id")!;
        var id = PackScopedId.Scoped(pack.PackId, localId);

        var triggerEvent = ReadSingleString(node, "event");
        if (string.IsNullOrWhiteSpace(triggerEvent))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/event",
                StateVitalsCodes.TriggerEventMissing,
                "trigger 必须声明触发事件名。",
                "加 \"event\": \"combat.hit\"。"));
        }

        var stateRaw = ReadSingleString(node, "state");
        if (string.IsNullOrWhiteSpace(stateRaw))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/state",
                StateVitalsCodes.TriggerStateNotAsserted,
                "trigger 必须声明它要断言哪个 assertedState。",
                "加 \"state\": \"core#injured\"。"));
        }

        var probability = 1d;
        if (node["probability"] is JsonValue probabilityValue && probabilityValue.TryGetValue<double>(out var parsed))
        {
            probability = parsed;
            if (probability is < 0 or > 1)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/probability",
                    StateVitalsCodes.TriggerProbabilityInvalid,
                    $"probability {Format(probability)} 越界。",
                    "取 [0, 1] 之间，例如 0.25。"));
            }
        }

        if (!TryParseDuration(node, pack, pointer, "durationHours", issues, out var duration))
            return null;

        if (issues.Count > before)
            return null;

        return new TriggerDefinition(
            id,
            pack.PackId,
            triggerEvent!,
            PackScopedId.Resolve(pack.PackId, stateRaw!),
            probability,
            duration,
            ReadSingleString(node, "mayRequire"));
    }

    // ------------------------------------------------------------------ shared validators

    private static bool TryReadRange(
        JsonNode node,
        ParsedPack pack,
        string pointer,
        string localId,
        List<PackIssue> issues,
        out VitalRange? range)
    {
        range = null;
        if (node["range"] is not JsonObject rangeNode
            || !TryReadDouble(rangeNode, "min", out var min)
            || !TryReadDouble(rangeNode, "max", out var max))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/range",
                StateVitalsCodes.DefinitionRangeInvalid,
                $"vital '{localId}' 缺少 range.min / range.max。",
                "加 \"range\": { \"min\": 0, \"max\": 100 }。"));
            return false;
        }

        if (!(max > min))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/range",
                StateVitalsCodes.DefinitionRangeInvalid,
                $"vital '{localId}' 的 range.max ({Format(max)}) 必须大于 range.min ({Format(min)})。",
                $"把 max 改成大于 {Format(min)} 的值。"));
            return false;
        }

        range = new VitalRange(min, max);
        return true;
    }

    private static VitalDecay? ParseDecay(
        JsonNode node,
        ParsedPack pack,
        string pointer,
        string localId,
        VitalRange range,
        List<PackIssue> issues)
    {
        if (node["decay"] is not JsonObject decayNode)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"vital '{localId}' 缺少 decay。",
                "加 \"decay\": { \"mode\": \"multiplicative\", \"ratePerHour\": 0.08, \"target\": 100 }。"));
            return null;
        }

        var mode = ReadSingleString(decayNode, "mode");
        if (!string.Equals(mode, "multiplicative", StringComparison.Ordinal))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/mode",
                StateVitalsCodes.DefinitionDecayModeUnsupported,
                $"decay.mode '{mode}' 不受支持。",
                "v1 只支持 \"mode\": \"multiplicative\"。"));
            return null;
        }

        if (!TryReadDouble(decayNode, "ratePerHour", out var rate) || rate <= 0 || rate > MaxRatePerHour)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/ratePerHour",
                StateVitalsCodes.DefinitionDecayRateOutOfRange,
                "ratePerHour 缺失或越界。",
                $"取 (0, {Format(MaxRatePerHour)}] 之间，例如 0.08。"));
            return null;
        }

        var target = ParseTarget(decayNode, pack, pointer, localId, range, issues);
        return target is null ? null : new VitalDecay(VitalDecayMode.Multiplicative, rate, target);
    }

    private static VitalTarget? ParseTarget(
        JsonNode decayNode,
        ParsedPack pack,
        string pointer,
        string localId,
        VitalRange range,
        List<PackIssue> issues)
    {
        var targetNode = decayNode["target"];
        if (targetNode is JsonValue constantValue && constantValue.TryGetValue<double>(out var constant))
        {
            if (constant < range.Min || constant > range.Max)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/decay/target",
                    StateVitalsCodes.DefinitionDecayTargetInvalid,
                    $"常量 target {Format(constant)} 不在 [{Format(range.Min)}, {Format(range.Max)}] 内。",
                    $"用范围内的值，例如 {Format(range.Max)}。"));
                return null;
            }

            return new VitalTarget.Constant(constant);
        }

        if (targetNode is not JsonObject cycleNode)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"vital '{localId}' 的 decay.target 必须是数字或周期对象。",
                "写常量（例如 100），或 { \"kind\": \"cycle\", \"min\": 10, \"max\": 90, \"periodHours\": 24, \"peakHourUtc\": 3 }。"));
            return null;
        }

        var kind = ReadSingleString(cycleNode, "kind");
        if (!string.Equals(kind, "cycle", StringComparison.Ordinal))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target/kind",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"周期 target 的 kind '{kind}' 不受支持。",
                "用 \"kind\": \"cycle\"。"));
            return null;
        }

        if (!TryReadDouble(cycleNode, "min", out var min)
            || !TryReadDouble(cycleNode, "max", out var max)
            || !TryReadDouble(cycleNode, "periodHours", out var period)
            || !TryReadDouble(cycleNode, "peakHourUtc", out var peak))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                "周期 target 需要 min / max / periodHours / peakHourUtc。",
                "例如 { \"kind\": \"cycle\", \"min\": 10, \"max\": 90, \"periodHours\": 24, \"peakHourUtc\": 3 }。"));
            return null;
        }

        if (min < range.Min || max > range.Max || !(max > min))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"周期区间 [{Format(min)}, {Format(max)}] 必须递增且落在 [{Format(range.Min)}, {Format(range.Max)}] 内。",
                "收紧 min/max。"));
            return null;
        }

        if (period is < 1 || period > MaxPeriodHours)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target/periodHours",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"periodHours {Format(period)} 越界。",
                $"取 [1, {MaxPeriodHours}] 之间（24 = 昼夜，672 = 四周）。"));
            return null;
        }

        if (peak is < 0 || peak >= period)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/decay/target/peakHourUtc",
                StateVitalsCodes.DefinitionDecayTargetInvalid,
                $"peakHourUtc {Format(peak)} 必须落在 [0, {Format(period)}) 内。",
                "把峰值放到周期内的某个小时。"));
            return null;
        }

        return new VitalTarget.Cycle(min, max, period, peak);
    }

    private static IReadOnlyList<VitalLevel>? ParseLevels(
        JsonNode node,
        ParsedPack pack,
        string pointer,
        string localId,
        VitalRange range,
        List<PackIssue> issues)
    {
        var before = issues.Count;
        if (node["levels"] is not JsonArray levelsArray || levelsArray.Count == 0)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/levels",
                StateVitalsCodes.DefinitionLevelMissing,
                $"vital '{localId}' 缺少 levels。",
                "至少给一个档位：\"levels\": [ { \"at\": 0, \"id\": \"sated\" } ]。"));
            return null;
        }

        var levels = new List<VitalLevel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var item in levelsArray)
        {
            var levelPointer = $"{pointer}/levels/{index}";
            index++;

            if (item is not JsonObject levelNode
                || !TryReadDouble(levelNode, "at", out var at)
                || ReadSingleString(levelNode, "id") is not { Length: > 0 } levelId)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    levelPointer,
                    StateVitalsCodes.DefinitionLevelIdInvalid,
                    "档位需要数字 at 和字符串 id。",
                    "例如 { \"at\": 60, \"id\": \"hungry\" }。"));
                continue;
            }

            if (!IsLocalId(levelId))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    levelPointer + "/id",
                    StateVitalsCodes.DefinitionLevelIdInvalid,
                    $"档位 id '{levelId}' 不是合法局部 id。",
                    "用小写字母/数字/._-，不带 pack 前缀。"));
            }

            if (!ids.Add(levelId))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    levelPointer + "/id",
                    StateVitalsCodes.DefinitionLevelDuplicate,
                    $"档位 id '{levelId}' 重复。",
                    "每个档位的 id 必须唯一。"));
            }

            if (at < range.Min || at > range.Max)
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    levelPointer + "/at",
                    StateVitalsCodes.DefinitionLevelOutOfRange,
                    $"档位 at {Format(at)} 超出 [{Format(range.Min)}, {Format(range.Max)}]。",
                    "把 at 收进 range 内。"));
            }

            levels.Add(new VitalLevel(
                levelId,
                at,
                ReadSingleString(levelNode, "label") ?? levelId,
                ReadSingleString(levelNode, "semantic"),
                ReadSingleString(levelNode, "template")));
        }

        if (levels.Count > 0 && Math.Abs(levels[0].At - range.Min) > 1e-9)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                $"{pointer}/levels/0/at",
                StateVitalsCodes.DefinitionLevelGap,
                $"第一个档位从 {Format(levels[0].At)} 开始，但 range 从 {Format(range.Min)} 开始（存在空洞）。",
                $"把第一个档位的 at 改为 {Format(range.Min)}。"));
        }

        for (var position = 1; position < levels.Count; position++)
        {
            if (!(levels[position].At > levels[position - 1].At))
            {
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    $"{pointer}/levels/{position}/at",
                    StateVitalsCodes.DefinitionLevelNotMonotonic,
                    $"档位 at={Format(levels[position].At)} 不大于前一个 at={Format(levels[position - 1].At)}（必须单调递增）。",
                    $"改为 at={Format(levels[position - 1].At + 10)} 或调整前一档。"));
            }
        }

        return issues.Count > before ? null : levels;
    }

    private static bool TryReadStacking(
        JsonNode node,
        ParsedPack pack,
        string pointer,
        string localId,
        List<PackIssue> issues,
        out VitalApplyRule rule)
    {
        rule = VitalApplyRule.Add;
        var stacking = ReadSingleString(node, "stacking");
        if (string.IsNullOrWhiteSpace(stacking))
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/stacking",
                StateVitalsCodes.DefinitionEffectStackingMissing,
                $"effect '{localId}' 没有声明 stacking（每条效果必须自己声明叠加规则）。",
                "加 \"stacking\": \"add\"（或 max / mul）。"));
            return false;
        }

        switch (stacking)
        {
            case "add":
                rule = VitalApplyRule.Add;
                return true;
            case "max":
                rule = VitalApplyRule.Max;
                return true;
            case "mul":
                rule = VitalApplyRule.Mul;
                return true;
            default:
                issues.Add(new PackIssue(
                    pack.DisplaySource,
                    pointer + "/stacking",
                    StateVitalsCodes.DefinitionEffectStackingUnknown,
                    $"stacking '{stacking}' 不受支持。",
                    "用 add / max / mul 之一。"));
                return false;
        }
    }

    private static bool TryParseDuration(
        JsonNode node,
        ParsedPack pack,
        string pointer,
        string property,
        List<PackIssue> issues,
        out TimeSpan? duration)
    {
        duration = null;
        if (node[property] is not JsonValue value)
            return true;

        if (!value.TryGetValue<double>(out var hours) || hours <= 0 || hours > 24 * 365)
        {
            issues.Add(new PackIssue(
                pack.DisplaySource,
                pointer + "/" + property,
                StateVitalsCodes.PackDurationInvalid,
                $"{property} 必须是 (0, 8760] 之间的小时数。",
                "例如 \"durationHours\": 8；不写表示永久。"));
            return false;
        }

        duration = TimeSpan.FromHours(hours);
        return true;
    }

    private static DescriptionVoice ParseVoice(JsonElement root, string display, List<PackIssue> issues)
    {
        if (!root.TryGetProperty("voice", out var voice) || voice.ValueKind != JsonValueKind.Object)
            return DescriptionVoice.Empty;

        var prompt = voice.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var tone = voice.TryGetProperty("tone", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var examples = ReadStringArray(voice, "examples", display, issues);
        var banned = ReadStringArray(voice, "banned", display, issues);

        return new DescriptionVoice(prompt, tone, examples, banned);
    }

    // ------------------------------------------------------------------ cross references

    private static void ValidateCrossReferences(
        IReadOnlyList<VitalDefinition> vitals,
        IReadOnlyList<AssertedStateDefinition> asserted,
        IReadOnlyList<DerivedStateDefinition> derived,
        IReadOnlyList<StateEffect> effects,
        IReadOnlyList<TriggerDefinition> triggers,
        List<PackIssue> issues,
        IReadOnlyList<ContentPack> packs)
    {
        var vitalIds = vitals.Select(vital => vital.Id).ToHashSet(StringComparer.Ordinal);
        var assertedIds = asserted.Select(state => state.Id).ToHashSet(StringComparer.Ordinal);
        var derivedIds = derived.Select(state => state.Id).ToHashSet(StringComparer.Ordinal);
        var stateIds = assertedIds.Concat(derivedIds).ToHashSet(StringComparer.Ordinal);

        foreach (var derivedState in derived)
        {
            var vital = vitals.FirstOrDefault(candidate => string.Equals(candidate.Id, derivedState.VitalId, StringComparison.Ordinal));
            if (vital is null)
            {
                issues.Add(new PackIssue(
                    SourceOf(packs, derivedState.PackId),
                    $"/derivedStates/{derivedState.LocalId}/vital",
                    StateVitalsCodes.DerivedVitalUnknown,
                    $"derivedState '{derivedState.Id}' 引用了不存在的 vital '{derivedState.VitalId}'。",
                    "改成已存在的 vital id（带 pack 前缀或本包的局部 id）。"));
                continue;
            }

            foreach (var level in derivedState.Levels)
            {
                if (vital.Levels.Any(candidate => string.Equals(candidate.Id, level, StringComparison.Ordinal)))
                    continue;

                issues.Add(new PackIssue(
                    SourceOf(packs, derivedState.PackId),
                    $"/derivedStates/{derivedState.LocalId}/levels",
                    StateVitalsCodes.DerivedLevelUnknown,
                    $"derivedState '{derivedState.Id}' 引用了 vital '{vital.Id}' 不存在的档位 '{level}'。",
                    $"可用档位：{string.Join(", ", vital.Levels.Select(candidate => candidate.Id))}。"));
            }
        }

        foreach (var effect in effects)
        {
            var source = SourceOf(packs, effect.PackId);

            // One-way graph: state → vital. Anything else is rejected, which makes cycles
            // unrepresentable rather than merely discouraged (design §3.1).
            if (vitalIds.Contains(effect.FromStateId))
            {
                issues.Add(new PackIssue(
                    source,
                    $"/effects/{effect.LocalId}/from",
                    StateVitalsCodes.EffectSourceMustBeState,
                    $"effect '{effect.Id}' 的来源 '{effect.FromStateId}' 是一个 vital，不是 state。",
                    "effect 只能从 asserted/derived state 指向 vital：改 from 为 state id。"));
            }
            else if (!stateIds.Contains(effect.FromStateId))
            {
                issues.Add(new PackIssue(
                    source,
                    $"/effects/{effect.LocalId}/from",
                    StateVitalsCodes.EffectSourceUnknown,
                    $"effect '{effect.Id}' 引用了不存在的 state '{effect.FromStateId}'。",
                    "先用 assertedStates 或 derivedStates 声明它。"));
            }

            if (stateIds.Contains(effect.ToVitalId))
            {
                issues.Add(new PackIssue(
                    source,
                    $"/effects/{effect.LocalId}/to",
                    StateVitalsCodes.EffectTargetMustBeVital,
                    $"effect '{effect.Id}' 的目标 '{effect.ToVitalId}' 是一个 state，不是 vital。",
                    "state 只能从 vital 派生：改 to 为 vital id。"));
            }
            else if (!vitalIds.Contains(effect.ToVitalId))
            {
                issues.Add(new PackIssue(
                    source,
                    $"/effects/{effect.LocalId}/to",
                    StateVitalsCodes.EffectTargetUnknown,
                    $"effect '{effect.Id}' 引用了不存在的 vital '{effect.ToVitalId}'。",
                    "先声明该 vital，或修正 id。"));
            }
        }

        foreach (var trigger in triggers)
        {
            if (assertedIds.Contains(trigger.StateId))
                continue;

            issues.Add(new PackIssue(
                SourceOf(packs, trigger.PackId),
                $"/triggers/{trigger.LocalId}/state",
                StateVitalsCodes.TriggerStateNotAsserted,
                $"trigger '{trigger.Id}' 指向 '{trigger.StateId}'，它不是 assertedState。",
                "trigger 只能断言 assertedState（derived 是只读视图）。"));
        }
    }

    private static void ValidateDuplicateIds(
        IReadOnlyList<VitalDefinition> vitals,
        IReadOnlyList<AssertedStateDefinition> asserted,
        IReadOnlyList<DerivedStateDefinition> derived,
        List<PackIssue> issues)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, kind) in vitals.Select(vital => (vital.Id, "vital"))
                     .Concat(asserted.Select(state => (state.Id, "assertedState")))
                     .Concat(derived.Select(state => (state.Id, "derivedState"))))
        {
            if (seen.TryAdd(id, kind))
                continue;

            issues.Add(new PackIssue(
                id,
                string.Empty,
                StateVitalsCodes.DefinitionDuplicate,
                $"id '{id}' 同时被声明为 {seen[id]} 和 {kind}。",
                "让每个 id 在同一包内只对应一种原语。"));
        }
    }

    private static string SourceOf(IReadOnlyList<ContentPack> packs, string packId)
        => packs.FirstOrDefault(pack => string.Equals(pack.PackId, packId, StringComparison.Ordinal))?.SourcePath ?? packId;

    // ------------------------------------------------------------------ json helpers

    /// <summary>
    /// Content ids are author-facing, so mixed case is allowed (<c>baseNeed</c>); they still may not
    /// carry a <c>#</c>, because that is the pack separator. <c>stateId</c> stays lowercase: it is a
    /// storage key, not content.
    /// </summary>
    private static bool IsLocalId(string? value)
        => !string.IsNullOrEmpty(value)
           && value.Length <= 64
           && char.IsAsciiLetterOrDigit(value[0])
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string? ReadSingleString(JsonNode? node, string property)
        => node?[property] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool? JsonBool(JsonNode? node, string property)
        => node?[property] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static bool TryReadDouble(JsonNode node, string property, out double value)
    {
        value = 0;
        if (node[property] is not JsonValue json || !json.TryGetValue<double>(out var parsed))
            return false;

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
            return false;

        value = parsed;
        return true;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode node, string property, string display, List<PackIssue> issues)
    {
        var values = new List<string>();
        if (node[property] is not JsonArray array)
            return values;

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                values.Add(text.Trim());
            else
                issues.Add(new PackIssue(display, "/" + property, StateVitalsCodes.DefinitionInvalidJson, $"{property} 只能包含字符串。", "删掉非字符串项。"));
        }

        return values;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property, string display, List<PackIssue> issues)
    {
        var values = new List<string>();
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!.Trim());
            else
                issues.Add(new PackIssue(display, "/" + property, StateVitalsCodes.DefinitionInvalidJson, $"{property} 只能包含字符串。", "删掉非字符串项。"));
        }

        return values;
    }

    private static string Format(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
