namespace Natter.Feature.Ai.DeepSeek;

/// <summary>In-memory provider metrics for <c>ai status</c>; never stores prompts or keys.</summary>
public sealed class DeepSeekMetrics
{
    private readonly Lock _gate = new();
    private readonly Queue<int> _latencies = new();
    private long _requests;
    private long _errors;
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedInputTokens;
    private long _reasoningTokens;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;

    public void RecordSuccess(DeepSeekUsage usage, int latencyMs)
    {
        lock (_gate)
        {
            _requests++;
            _inputTokens += usage.PromptTokens;
            _outputTokens += usage.CompletionTokens;
            _cachedInputTokens += usage.PromptCacheHitTokens;
            _reasoningTokens += usage.ReasoningTokens;
            _latencies.Enqueue(Math.Max(0, latencyMs));
            while (_latencies.Count > 256)
                _latencies.Dequeue();
        }
    }

    public void RecordError(DeepSeekErrorKind kind, string message)
    {
        lock (_gate)
        {
            _errors++;
            _lastError = $"{kind}: {message}";
            _lastErrorAt = DateTimeOffset.UtcNow;
        }
    }

    public DeepSeekMetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var sorted = _latencies.OrderBy(value => value).ToArray();
            return new DeepSeekMetricsSnapshot(
                _requests,
                _errors,
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                _inputTokens,
                _outputTokens,
                _cachedInputTokens,
                _reasoningTokens,
                _lastError,
                _lastErrorAt);
        }
    }

    private static int? Percentile(int[] values, double percentile)
    {
        if (values.Length == 0)
            return null;

        var index = (int)Math.Ceiling(percentile * values.Length) - 1;
        return values[Math.Clamp(index, 0, values.Length - 1)];
    }
}

public sealed record DeepSeekMetricsSnapshot(
    long Requests,
    long Errors,
    int? P50LatencyMs,
    int? P95LatencyMs,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    string? LastError,
    DateTimeOffset? LastErrorAt);
