namespace Natter.Feature.Ai.Spark;

using Natter.Feature.Ai;

public sealed class SparkMetrics
{
    private readonly Lock _gate = new();
    private readonly Queue<int> _latencies = new();
    private long _requests;
    private long _errors;
    private long _input;
    private long _output;
    private long _cached;
    private long _reasoning;
    private string? _lastError;

    public void RecordSuccess(SparkUsage usage, int latencyMs)
    {
        lock (_gate)
        {
            _requests++;
            _input += usage.PromptTokens;
            _output += usage.CompletionTokens;
            _cached += usage.CachedTokens;
            _reasoning += usage.ReasoningTokens;
            _latencies.Enqueue(Math.Max(0, latencyMs));
            while (_latencies.Count > 256)
                _latencies.Dequeue();
        }
    }

    public void RecordError(AiErrorKind kind, string message)
    {
        lock (_gate)
        {
            _errors++;
            _lastError = $"{kind}: {message}";
        }
    }

    public SparkMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var sorted = _latencies.OrderBy(value => value).ToArray();
            return new SparkMetricsSnapshot(
                _requests,
                _errors,
                sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1],
                sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1],
                _input,
                _output,
                _cached,
                _reasoning,
                _lastError);
        }
    }
}

public sealed record SparkMetricsSnapshot(
    long Requests,
    long Errors,
    int? P50LatencyMs,
    int? P95LatencyMs,
    long InputTokens,
    long OutputTokens,
    long CachedTokens,
    long ReasoningTokens,
    string? LastError);
