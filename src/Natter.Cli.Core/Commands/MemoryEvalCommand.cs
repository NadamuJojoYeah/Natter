using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Natter.Application.Chat;
using Natter.Chat;
using Natter.Cli.Abstractions;
using Natter.Infrastructure.Chat;

namespace Natter.Cli.Core.Commands;

// ===================================================================================================
// natter memory eval — P6.9 calibration harness (docs/P6_MEMORY_SYSTEM.md §12).
//
// Purpose: replace four guessed numbers (decay S0 scale, maxCandidates, maxInjected, maxTokens) with
// measured ones. The fixture, the corpus ages and "now" are all fixed, so two runs produce identical
// ranks/passes; only the millisecond column varies.
//
// Measurement knobs (tokenizer / decay / candidates) are NOT production configuration: they live on
// MemoryEntryStoreOptions ([EditorBrowsable(Never)]) and are never accepted from a profile.
// ===================================================================================================

/// <summary>One seeded memory in the eval corpus.</summary>
public sealed record MemoryEvalCorpusEntry(
    string Id,
    MemoryType Type,
    string Content,
    double AgeDays);

/// <summary>One recall assertion.</summary>
public sealed record MemoryEvalCase(
    string Id,
    string Category,
    string Query,
    IReadOnlyList<string> Expect,
    bool ExpectEmpty,
    string? Note);

/// <summary>Parsed fixture file.</summary>
public sealed record MemoryEvalFixture(
    int SchemaVersion,
    IReadOnlyList<MemoryEvalCorpusEntry> Corpus,
    IReadOnlyList<MemoryEvalCase> Cases)
{
    /// <summary>Parses the JSON fixture. Unknown <c>type</c> values are rejected, not defaulted.</summary>
    public static MemoryEvalFixture Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;

        var schemaVersion = root.TryGetProperty("schemaVersion", out var versionElement)
            ? versionElement.GetInt32()
            : 1;

        var corpus = new List<MemoryEvalCorpusEntry>();
        if (root.TryGetProperty("corpus", out var corpusElement))
        {
            foreach (var item in corpusElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()
                         ?? throw new InvalidOperationException("corpus entry is missing 'id'");
                var typeText = item.GetProperty("type").GetString();
                if (!MemoryEntryMapping.TryParseStorageValue(typeText, out var type))
                    throw new InvalidOperationException($"corpus entry '{id}' has unknown type '{typeText}'");

                corpus.Add(new MemoryEvalCorpusEntry(
                    id,
                    type,
                    item.GetProperty("content").GetString() ?? string.Empty,
                    item.TryGetProperty("ageDays", out var ageElement) ? ageElement.GetDouble() : 0d));
            }
        }

        var cases = new List<MemoryEvalCase>();
        if (root.TryGetProperty("cases", out var casesElement))
        {
            foreach (var item in casesElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()
                         ?? throw new InvalidOperationException("case is missing 'id'");

                var expect = new List<string>();
                if (item.TryGetProperty("expect", out var expectElement))
                {
                    foreach (var entry in expectElement.EnumerateArray())
                    {
                        var value = entry.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            expect.Add(value);
                    }
                }

                cases.Add(new MemoryEvalCase(
                    id,
                    item.TryGetProperty("category", out var categoryElement)
                        ? categoryElement.GetString() ?? "unknown"
                        : "unknown",
                    item.GetProperty("query").GetString() ?? string.Empty,
                    expect,
                    item.TryGetProperty("expectEmpty", out var emptyElement) && emptyElement.GetBoolean(),
                    item.TryGetProperty("note", out var noteElement) ? noteElement.GetString() : null));
            }
        }

        if (cases.Count == 0)
            throw new InvalidOperationException("fixture contains no cases");

        return new MemoryEvalFixture(schemaVersion, corpus, cases);
    }
}

/// <summary>One measurement configuration.</summary>
public sealed record MemoryEvalConfig(
    string Label,
    MemoryFtsTokenization Tokenizer,
    string DecayMode,
    int Candidates)
{
    public bool ApplyDecay => DecayMode != "off";

    public double StabilityScale => DecayMode switch
    {
        "long" => 3d,
        _ => 1d,
    };

    public MemoryEntryStoreOptions ToStoreOptions(DateTimeOffset now) => new()
    {
        Tokenization = Tokenizer,
        ApplyDecay = ApplyDecay,
        StabilityScale = StabilityScale,
        FixedNow = now,
    };

    public string TokenizerLabel => Tokenizer == MemoryFtsTokenization.RawTrigram ? "trigram" : "pretokenized";
}

/// <summary>One case result.</summary>
public sealed record MemoryEvalTrial(
    string CaseId,
    string Category,
    string Query,
    IReadOnlyList<string> Expected,
    IReadOnlyList<string> Actual,
    int Rank,
    bool Pass,
    double ElapsedMs,
    IReadOnlyList<MemoryEvalCandidate> Candidates);

/// <summary>One candidate the store returned, with the terms that explain its position.</summary>
public sealed record MemoryEvalCandidate(
    string Id,
    MemoryType Type,
    double AgeDays,
    double StabilityDays,
    double Retention,
    int Order);

/// <summary>Result of one configuration.</summary>
public sealed record MemoryEvalReport(
    MemoryEvalConfig Config,
    int Total,
    int Passed,
    double Recall,
    double Mrr,
    double AverageMs,
    IReadOnlyList<MemoryEvalTrial> Trials)
{
    /// <summary>
    /// Stable identity of the run's *decisions* (order excluded, timing excluded). Two runs of the
    /// same fixture and configuration must produce the same fingerprint.
    /// </summary>
    public string Fingerprint => string.Join(
        "\n",
        Trials.Select(trial =>
            $"{trial.CaseId}|{trial.Rank}|{trial.Pass}|{string.Join(",", trial.Actual)}"));
}

/// <summary>Runs the fixture against one configuration using an isolated temporary database.</summary>
public static class MemoryEvalRunner
{
    /// <summary>Fixed instant so decay maths and case ordering are reproducible run to run.</summary>
    public static readonly DateTimeOffset EvalNow = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static readonly Source EvalSource = Source.Create("eval", "eval", "harness");
    private static readonly Conversation EvalConversation = new("eval-conversation", ConversationKind.Direct);
    private static readonly User EvalUser = new("eval-user", "Eval User");

    public static async ValueTask<MemoryEvalReport> RunAsync(
        MemoryEvalFixture fixture,
        MemoryEvalConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(config);

        var directory = Path.Combine(
            Path.GetTempPath(),
            "natter-memory-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new SqliteMemoryEntryStore(
                Path.Combine(directory, "eval.db"),
                config.ToStoreOptions(EvalNow));

            var labels = await SeedAsync(store, fixture.Corpus, directory, cancellationToken).ConfigureAwait(false);

            var trials = new List<MemoryEvalTrial>(fixture.Cases.Count);
            var reciprocalRanks = new List<double>();

            foreach (var evalCase in fixture.Cases)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var hits = await store.SearchAsync(
                    new MemoryEntryQuery(EvalSource, EvalConversation, EvalUser)
                    {
                        Query = evalCase.Query,
                        Limit = config.Candidates,
                        IncludeArchived = false,
                    },
                    cancellationToken).ConfigureAwait(false);
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                // The store assigns generated ids; the fixture refers to corpus ids, so translate
                // back for reporting and for the expectation check.
                var actual = hits.Select(hit => Label(labels, hit.Id)).ToArray();
                var rank = 0;
                foreach (var expected in evalCase.Expect)
                {
                    var index = Array.IndexOf(actual, expected);
                    if (index < 0)
                        continue;
                    rank = rank == 0 ? index + 1 : Math.Min(rank, index + 1);
                }

                var pass = evalCase.ExpectEmpty
                    ? actual.Length == 0
                    : evalCase.Expect.Count > 0 && evalCase.Expect.All(id => actual.Contains(id, StringComparer.Ordinal));

                if (!evalCase.ExpectEmpty && rank > 0)
                    reciprocalRanks.Add(1d / rank);
                else if (!evalCase.ExpectEmpty)
                    reciprocalRanks.Add(0d);

                trials.Add(new MemoryEvalTrial(
                    evalCase.Id,
                    evalCase.Category,
                    evalCase.Query,
                    evalCase.Expect,
                    actual,
                    rank,
                    pass,
                    elapsedMs,
                    hits.Select((hit, order) => new MemoryEvalCandidate(
                        Label(labels, hit.Id),
                        hit.Type,
                        Math.Max(0, (EvalNow - hit.UpdatedAt).TotalDays),
                        hit.StabilityDays,
                        RetentionFor(hit, config),
                        order + 1)).ToArray()));
            }

            var passed = trials.Count(trial => trial.Pass);
            var total = trials.Count;
            var mrr = reciprocalRanks.Count == 0 ? 0d : reciprocalRanks.Average();
            var averageMs = trials.Count == 0 ? 0d : trials.Average(trial => trial.ElapsedMs);

            return new MemoryEvalReport(
                config,
                total,
                passed,
                total == 0 ? 0d : (double)passed / total,
                mrr,
                averageMs,
                trials);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Retention exactly as the configured run computes it. The diagnostics must use this, not
    /// <see cref="MemoryEntry.WeightAt"/>, otherwise a "decay off" run would print decayed numbers.
    /// </summary>
    public static double RetentionFor(MemoryEntry entry, MemoryEvalConfig config)
    {
        if (!config.ApplyDecay)
            return 1d;

        return MemoryDecay.Retention(
            Math.Max(0, (EvalNow - entry.UpdatedAt).TotalDays),
            entry.StabilityDays * config.StabilityScale,
            MemoryDecay.ProfileFor(entry.Type).DecayD);
    }

    /// <summary>Returns the stored id → corpus id map so reports speak the fixture's language.</summary>
    private static async ValueTask<Dictionary<string, string>> SeedAsync(
        SqliteMemoryEntryStore store,
        IReadOnlyList<MemoryEvalCorpusEntry> corpus,
        string directory,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (corpus.Count == 0)
            return labels;

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in corpus)
        {
            var written = await store.WriteAsync(
                new MemoryEntryWriteRequest(
                    EvalSource,
                    EvalConversation,
                    EvalUser,
                    entry.Type,
                    entry.Content),
                cancellationToken).ConfigureAwait(false);
            ids[entry.Id] = written.Id;
            labels[written.Id] = entry.Id;
        }

        // Age the rows after writing: the decay maths reads updated_at, and writing always stamps
        // "now". Ages are relative to the fixed EvalNow so two runs are identical.
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "eval.db"),
            }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in corpus)
        {
            var stamped = EvalNow.AddDays(-entry.AgeDays).ToString("O", CultureInfo.InvariantCulture);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE memory_entries SET created_at = $stamped, updated_at = $stamped
                WHERE entry_id = $id;
                """;
            command.Parameters.AddWithValue("$stamped", stamped);
            command.Parameters.AddWithValue("$id", ids[entry.Id]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return labels;
    }

    /// <summary>Translates a stored id to the fixture's corpus id (unknown ids pass through).</summary>
    private static string Label(Dictionary<string, string> labels, string storedId)
        => labels.TryGetValue(storedId, out var label) ? label : storedId;
}

/// <summary>Text rendering for the CLI. Pure functions so output can be asserted in tests.</summary>
public static class MemoryEvalRenderer
{
    public static string RenderReport(MemoryEvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();

        builder.AppendLine(
            $"config: tokenizer={report.Config.TokenizerLabel} decay={report.Config.DecayMode} candidates={report.Config.Candidates}");
        builder.AppendLine("case                     category    rank  pass  ms");
        builder.AppendLine(new string('-', 60));

        foreach (var trial in report.Trials)
        {
            var rank = trial.Rank == 0 ? "-" : trial.Rank.ToString(CultureInfo.InvariantCulture);
            var pass = trial.Pass ? "OK" : "FAIL";
            builder.AppendLine(
                $"{Fit(trial.CaseId, 24)} {Fit(trial.Category, 11)} {Fit(rank, 5)} {Fit(pass, 5)} {trial.ElapsedMs.ToString("F1", CultureInfo.InvariantCulture)}");
        }

        builder.AppendLine(new string('-', 60));
        builder.AppendLine(
            $"total: {report.Passed}/{report.Total} ({report.Recall * 100:F1}%)  MRR: {report.Mrr.ToString("F3", CultureInfo.InvariantCulture)}  avg: {report.AverageMs.ToString("F1", CultureInfo.InvariantCulture)}ms");

        var failures = report.Trials.Where(trial => !trial.Pass).ToArray();
        if (failures.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("failures:");
            foreach (var trial in failures)
                AppendFailure(builder, trial);
        }

        return builder.ToString();
    }

    /// <summary>Side-by-side summary for <c>--compare</c>.</summary>
    public static string RenderComparison(IReadOnlyList<MemoryEvalReport> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("comparison");
        builder.AppendLine("config                                   recall   MRR     avg ms");
        builder.AppendLine(new string('-', 70));

        foreach (var report in reports)
        {
            var label = report.Config.Label;
            builder.AppendLine(
                $"{Fit(label, 40)} {report.Recall * 100,5:F1}%  {report.Mrr.ToString("F3", CultureInfo.InvariantCulture),5}   {report.AverageMs.ToString("F1", CultureInfo.InvariantCulture),6}");
        }

        // Per-case diff against the first (baseline) configuration.
        var baseline = reports[0];
        builder.AppendLine();
        builder.AppendLine($"per-case rank (baseline = {baseline.Config.Label}, 'x' = not found)");
        builder.AppendLine($"{Fit("case", 24)} {string.Join(" ", reports.Select(r => Fit(ShortLabel(r.Config), 6)))}");

        foreach (var trial in baseline.Trials)
        {
            var cells = reports.Select(report =>
            {
                var match = report.Trials.FirstOrDefault(candidate => candidate.CaseId == trial.CaseId);
                if (match is null)
                    return Fit("-", 6);
                return Fit(match.Rank == 0 ? "x" : match.Rank.ToString(CultureInfo.InvariantCulture), 6);
            });
            var flag = baseline.Trials.First(candidate => candidate.CaseId == trial.CaseId).Pass ? " " : "*";
            builder.AppendLine($"{Fit(trial.CaseId, 24)} {string.Join(" ", cells)}{flag}");
        }

        builder.AppendLine("(* = failing in baseline)");
        return builder.ToString();
    }

    private static void AppendFailure(StringBuilder builder, MemoryEvalTrial trial)
    {
        var expected = trial.Expected.Count == 0 ? "[]" : "[" + string.Join(",", trial.Expected) + "]";
        var actual = trial.Actual.Count == 0 ? "[]" : "[" + string.Join(",", trial.Actual) + "]";
        builder.AppendLine($"  {trial.CaseId}  query=\"{trial.Query}\" expected={expected} got={actual}");

        if (trial.Candidates.Count == 0)
        {
            builder.AppendLine("    candidates: (none — the index returned no row for this query)");
            return;
        }

        builder.AppendLine("    candidates:");
        foreach (var candidate in trial.Candidates)
        {
            builder.AppendLine(
                $"      #{candidate.Order} {Fit(candidate.Id, 16)} {Fit(candidate.Type.ToString().ToLowerInvariant(), 11)} age={candidate.AgeDays.ToString("F1", CultureInfo.InvariantCulture)}d S={candidate.StabilityDays.ToString("F1", CultureInfo.InvariantCulture)} retention={candidate.Retention.ToString("F4", CultureInfo.InvariantCulture)}");
        }
    }

    private static string ShortLabel(MemoryEvalConfig config) => config.DecayMode == "default"
        ? config.TokenizerLabel
        : config.DecayMode;

    private static string Fit(string? value, int width)
    {
        var text = value ?? string.Empty;
        if (text.Length > width)
            return text[..Math.Max(0, width - 1)] + "~";
        return text.PadRight(width);
    }
}

/// <summary><c>natter memory eval</c>: measure recall quality against a fixed fixture.</summary>
public sealed class MemoryEvalCommand : ICliCommandHandler
{
    private static readonly string[] TokenizerValues = ["pretokenized", "trigram"];
    private static readonly string[] DecayValues = ["default", "long", "off"];

    public async ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var path = invocation.GetOption("cases");
        if (string.IsNullOrWhiteSpace(path))
        {
            invocation.Console.Error.WriteLine("usage: memory eval --cases <fixture.json> [--compare tokenizer=...,...]");
            invocation.Console.Error.WriteLine("       --compare tokenizer=pretokenized,trigram | decay=default,long,off | candidates=20,50");
            return CliExitCodes.Usage;
        }

        if (!File.Exists(path))
        {
            invocation.Console.Error.WriteLine($"memory eval: fixture not found: {path}");
            return CliExitCodes.Usage;
        }

        MemoryEvalFixture fixture;
        try
        {
            fixture = MemoryEvalFixture.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            invocation.Console.Error.WriteLine($"memory eval: invalid fixture: {exception.Message}");
            return CliExitCodes.Usage;
        }

        List<MemoryEvalConfig> configurations;
        try
        {
            if (!TryBuildConfigurations(invocation, out configurations, out var error))
            {
                invocation.Console.Error.WriteLine($"memory eval: {error}");
                return CliExitCodes.Usage;
            }
        }
        catch (FormatException exception)
        {
            invocation.Console.Error.WriteLine($"memory eval: {exception.Message}");
            return CliExitCodes.Usage;
        }

        var reports = new List<MemoryEvalReport>(configurations.Count);
        foreach (var configuration in configurations)
        {
            var report = await MemoryEvalRunner.RunAsync(fixture, configuration, cancellationToken).ConfigureAwait(false);
            reports.Add(report);
            if (configurations.Count == 1)
                invocation.Console.Out.WriteLine(MemoryEvalRenderer.RenderReport(report));
        }

        if (reports.Count > 1)
        {
            foreach (var report in reports)
                invocation.Console.Out.WriteLine(MemoryEvalRenderer.RenderReport(report));
            invocation.Console.Out.WriteLine(MemoryEvalRenderer.RenderComparison(reports));
        }

        // Non-zero when the baseline configuration misses the documented gate, so CI can act on it.
        var baselineReport = reports[0];
        var gatePassed = baselineReport.Recall >= 0.85d && baselineReport.Mrr >= 0.8d;
        if (!gatePassed)
        {
            invocation.Console.Out.WriteLine(
                $"gate: recall {baselineReport.Recall * 100:F1}% / MRR {baselineReport.Mrr:F3} below 85% / 0.8 (§12)");
        }

        return gatePassed ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    /// <summary>
    /// Builds the configuration list. Only one <c>--compare</c> dimension per invocation: mixing
    /// dimensions would multiply runs and make the output harder to read than the decision needs.
    /// </summary>
    private static bool TryBuildConfigurations(
        CliInvocation invocation,
        out List<MemoryEvalConfig> configurations,
        out string? error)
    {
        configurations = [];
        error = null;

        var baseline = new MemoryEvalConfig("baseline", MemoryFtsTokenization.Pretokenized, "default", 20);

        // Explicit single-run overrides still apply when --compare is absent.
        var tokenizer = invocation.GetOption("tokenizer");
        if (!string.IsNullOrWhiteSpace(tokenizer))
        {
            if (!TryParseTokenizer(tokenizer, out var parsed))
            {
                error = $"unknown tokenizer '{tokenizer}' (expected pretokenized|trigram)";
                return false;
            }

            baseline = baseline with { Tokenizer = parsed, Label = "tokenizer=" + tokenizer };
        }

        var decay = invocation.GetOption("decay");
        if (!string.IsNullOrWhiteSpace(decay))
        {
            if (!DecayValues.Contains(decay, StringComparer.Ordinal))
            {
                error = $"unknown decay '{decay}' (expected default|long|off)";
                return false;
            }

            baseline = baseline with { DecayMode = decay, Label = "decay=" + decay };
        }

        var candidates = invocation.GetOption("candidates");
        if (!string.IsNullOrWhiteSpace(candidates))
        {
            if (!int.TryParse(candidates, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCandidates)
                || parsedCandidates <= 0)
            {
                error = $"invalid candidates '{candidates}' (expected a positive integer)";
                return false;
            }

            baseline = baseline with { Candidates = parsedCandidates, Label = "candidates=" + parsedCandidates };
        }

        var compare = invocation.GetOption("compare");
        if (string.IsNullOrWhiteSpace(compare))
        {
            configurations.Add(baseline with { Label = DescribeConfig(baseline) });
            return true;
        }

        var separator = compare.IndexOf('=');
        if (separator <= 0)
        {
            error = $"invalid --compare '{compare}' (expected <dimension>=<v1>,<v2>)";
            return false;
        }

        var dimension = compare[..separator].Trim().ToLowerInvariant();
        var values = compare[(separator + 1)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            error = $"invalid --compare '{compare}' (no values)";
            return false;
        }

        foreach (var value in values)
        {
            switch (dimension)
            {
                case "tokenizer":
                    if (!TryParseTokenizer(value, out var parsedTokenizer))
                    {
                        error = $"unknown tokenizer '{value}' (expected pretokenized|trigram)";
                        return false;
                    }

                    configurations.Add(baseline with
                    {
                        Tokenizer = parsedTokenizer,
                        Label = $"tokenizer={value}",
                    });
                    break;

                case "decay":
                    if (!DecayValues.Contains(value, StringComparer.Ordinal))
                    {
                        error = $"unknown decay '{value}' (expected default|long|off)";
                        return false;
                    }

                    configurations.Add(baseline with { DecayMode = value, Label = $"decay={value}" });
                    break;

                case "candidates":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCandidates)
                        || parsedCandidates <= 0)
                    {
                        error = $"invalid candidates '{value}' (expected a positive integer)";
                        return false;
                    }

                    configurations.Add(baseline with { Candidates = parsedCandidates, Label = $"candidates={value}" });
                    break;

                default:
                    error = $"unknown --compare dimension '{dimension}' (expected tokenizer|decay|candidates)";
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseTokenizer(string value, out MemoryFtsTokenization tokenization)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "pretokenized":
                tokenization = MemoryFtsTokenization.Pretokenized;
                return true;
            case "trigram":
                tokenization = MemoryFtsTokenization.RawTrigram;
                return true;
            default:
                tokenization = MemoryFtsTokenization.Pretokenized;
                return false;
        }
    }

    private static string DescribeConfig(MemoryEvalConfig config)
        => $"tokenizer={config.TokenizerLabel} decay={config.DecayMode} candidates={config.Candidates}";
}
