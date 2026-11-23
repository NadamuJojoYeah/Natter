using Natter.Application.Chat;
using Natter.Cli.Core.Commands;
using Natter.Infrastructure.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P6.9 evaluation harness (docs/P6_MEMORY_SYSTEM.md §12): fixture parsing, reproducibility, and the
/// tokenizer comparison the harness exists to quantify.
/// </summary>
public sealed class MemoryEvalTests
{
    [Fact]
    public void Fixture_parses_the_checked_in_corpus_and_cases()
    {
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));

        Assert.Equal(1, fixture.SchemaVersion);
        Assert.Equal(12, fixture.Corpus.Count);
        Assert.Equal(10, fixture.Cases.Count);

        // All five types plus the cross-type cases must be present (§12).
        foreach (var type in new[]
                 {
                     MemoryType.Policy, MemoryType.Preference, MemoryType.Fact,
                     MemoryType.Episodic, MemoryType.Trace,
                 })
        {
            Assert.Contains(fixture.Corpus, entry => entry.Type == type);
        }

        Assert.Contains(fixture.Cases, evalCase => evalCase.ExpectEmpty);
        Assert.Contains(fixture.Cases, evalCase => evalCase.Category == "CrossType");
        Assert.All(fixture.Corpus, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Content)));
    }

    [Fact]
    public void Fixture_rejects_an_unknown_memory_type_instead_of_defaulting()
    {
        const string json = """
            { "schemaVersion": 1,
              "corpus": [ { "id": "x", "type": "walrus", "content": "nope", "ageDays": 0 } ],
              "cases":  [ { "id": "c", "query": "q", "expect": [] } ] }
            """;

        var error = Assert.Throws<InvalidOperationException>(() => MemoryEvalFixture.Parse(json));
        Assert.Contains("walrus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_rejects_an_empty_case_list()
    {
        const string json = """{ "schemaVersion": 1, "corpus": [], "cases": [] }""";
        Assert.Throws<InvalidOperationException>(() => MemoryEvalFixture.Parse(json));
    }

    [Fact]
    public async Task Two_runs_of_the_same_configuration_produce_identical_decisions()
    {
        // Reproducibility is what makes the numbers actionable: same fixture + same configuration
        // must yield the same ranks and the same hit lists. Timing is deliberately excluded, and the
        // runner pins "now" (MemoryEvalRunner.EvalNow) so decay cannot drift between runs.
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));
        var config = new MemoryEvalConfig("baseline", MemoryFtsTokenization.Pretokenized, "default", 20);

        var first = await MemoryEvalRunner.RunAsync(fixture, config);
        var second = await MemoryEvalRunner.RunAsync(fixture, config);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Total, second.Total);
        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.Mrr, second.Mrr, 12);
    }

    [Fact]
    public async Task Pretokenized_index_ranks_two_character_chinese_far_above_trigram()
    {
        // This is the Q8 measurement. trigram cannot anchor two-character words, so the same query
        // lands deep in the list or is only reachable through the recency fallback.
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));

        var pretokenized = await MemoryEvalRunner.RunAsync(
            fixture,
            new MemoryEvalConfig("pretokenized", MemoryFtsTokenization.Pretokenized, "default", 20));
        var trigram = await MemoryEvalRunner.RunAsync(
            fixture,
            new MemoryEvalConfig("trigram", MemoryFtsTokenization.RawTrigram, "default", 20));

        Assert.True(
            pretokenized.Mrr > trigram.Mrr,
            $"pretokenized MRR {pretokenized.Mrr:F3} should beat trigram {trigram.Mrr:F3}");

        // The two-character cases are the ones that regress; assert it by name so the reason is visible.
        foreach (var caseId in new[] { "policy-language", "fact-city" })
        {
            var pre = pretokenized.Trials.First(trial => trial.CaseId == caseId);
            var tri = trigram.Trials.First(trial => trial.CaseId == caseId);
            Assert.Equal(1, pre.Rank);
            Assert.True(
                tri.Rank == 0 || tri.Rank > pre.Rank,
                $"{caseId}: trigram rank {tri.Rank} should be worse than pretokenized rank {pre.Rank}");
        }
    }

    [Fact]
    public async Task Recall_path_cannot_return_nothing_so_expect_empty_fails()
    {
        // KNOWN LIMITATION, fenced on purpose. When the FTS index has no hit the store falls back to
        // recency (P6.1 legacy behaviour), so a query with zero lexical overlap returns the most
        // recent entries instead of an empty list. For the active-injection path that is tolerable;
        // for a future memory_search tool it is wrong (it would hand the model unrelated memories).
        // This test fails loudly the day the fallback becomes configurable, so the new behaviour gets
        // a deliberate decision instead of silently changing the eval baseline.
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));
        var report = await MemoryEvalRunner.RunAsync(
            fixture,
            new MemoryEvalConfig("baseline", MemoryFtsTokenization.Pretokenized, "default", 20));

        var unrelated = report.Trials.First(trial => trial.CaseId == "unrelated");
        Assert.False(unrelated.Pass);
        Assert.NotEmpty(unrelated.Actual);
        Assert.Equal(fixture.Corpus.Count, unrelated.Actual.Count);
    }

    [Fact]
    public async Task Failing_case_reports_its_candidates_with_retention()
    {
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));
        var report = await MemoryEvalRunner.RunAsync(
            fixture,
            new MemoryEvalConfig("baseline", MemoryFtsTokenization.Pretokenized, "default", 5));

        var rendered = MemoryEvalRenderer.RenderReport(report);

        Assert.Contains("failures:", rendered, StringComparison.Ordinal);
        Assert.Contains("candidates:", rendered, StringComparison.Ordinal);
        // Diagnostics must show age / stability / retention, otherwise "why did this rank here" is
        // unanswerable without re-running with extra logging.
        Assert.Contains("retention=", rendered, StringComparison.Ordinal);
        Assert.Contains("age=", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_use_the_configured_decay_not_the_default_curve()
    {
        // A "decay off" run must print retention=1.0000. If the renderer used MemoryEntry.WeightAt
        // it would print decayed numbers and the diagnostic would lie about the run.
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));
        var report = await MemoryEvalRunner.RunAsync(
            fixture,
            new MemoryEvalConfig("off", MemoryFtsTokenization.Pretokenized, "off", 5));

        var unrelated = report.Trials.First(trial => trial.CaseId == "unrelated");
        Assert.All(unrelated.Candidates, candidate => Assert.Equal(1d, candidate.Retention, 12));

        var rendered = MemoryEvalRenderer.RenderReport(report);
        Assert.Contains("retention=1.0000", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("retention=0.9", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default", true, 1d)]
    [InlineData("long", true, 3d)]
    [InlineData("off", false, 1d)]
    public void Decay_mode_maps_to_store_options(string mode, bool applyDecay, double scale)
    {
        var config = new MemoryEvalConfig("probe", MemoryFtsTokenization.Pretokenized, mode, 20);
        Assert.Equal(applyDecay, config.ApplyDecay);
        Assert.Equal(scale, config.StabilityScale);

        var options = config.ToStoreOptions(MemoryEvalRunner.EvalNow);
        Assert.Equal(applyDecay, options.ApplyDecay);
        Assert.Equal(scale, options.StabilityScale);
        Assert.Equal(MemoryEvalRunner.EvalNow, options.FixedNow);
    }

    [Fact]
    public async Task Comparison_renders_one_row_per_configuration_and_flags_failures()
    {
        var fixture = MemoryEvalFixture.Parse(File.ReadAllText(FixturePath()));
        var reports = new List<MemoryEvalReport>();
        foreach (var tokenization in new[] { MemoryFtsTokenization.Pretokenized, MemoryFtsTokenization.RawTrigram })
        {
            reports.Add(await MemoryEvalRunner.RunAsync(fixture, new MemoryEvalConfig(
                tokenization == MemoryFtsTokenization.RawTrigram ? "tokenizer=trigram" : "tokenizer=pretokenized",
                tokenization,
                "default",
                20)));
        }

        var rendered = MemoryEvalRenderer.RenderComparison(reports);

        Assert.Contains("comparison", rendered, StringComparison.Ordinal);
        Assert.Contains("per-case rank", rendered, StringComparison.Ordinal);
        Assert.Contains("tokenizer=pretokenized", rendered, StringComparison.Ordinal);
        Assert.Contains("tokenizer=trigram", rendered, StringComparison.Ordinal);
        Assert.Contains("* = failing in baseline", rendered, StringComparison.Ordinal);
    }

    private static string FixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "tests", "fixtures", "memory-retrieval-cases.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("tests/fixtures/memory-retrieval-cases.json not found above the test binary");
    }
}
