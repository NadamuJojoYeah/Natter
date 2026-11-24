using System.Text;
namespace Natter.Application.Chat;

public sealed record EmbeddingPlannedInput(int Index, string Text, bool Truncated);

/// <summary>Batch plan preserving original order and recording truncated input indexes.</summary>
public sealed record EmbeddingPlan(
    IReadOnlyList<IReadOnlyList<EmbeddingPlannedInput>> Batches,
    IReadOnlyList<int> TruncatedIndexes);

/// <summary>
/// Host-side batching/truncation. Token estimation is deliberately conservative: CJK characters
/// count as 1.5 chars/token and all other characters as 4 chars/token, with a 10% budget reserve.
/// </summary>
public static class EmbeddingPlanner
{
    public static EmbeddingPlan CreatePlan(
        IReadOnlyList<string> inputs,
        int maxBatchSize,
        int maxInputTokens)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (maxBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "MaxBatchSize must be positive.");

        var tokenBudget = Math.Max(1, maxInputTokens);
        var truncated = new List<int>();
        var planned = new List<EmbeddingPlannedInput>(inputs.Count);

        for (var index = 0; index < inputs.Count; index++)
        {
            var original = inputs[index] ?? string.Empty;
            var text = Truncate(original, tokenBudget, out var wasTruncated);
            if (wasTruncated)
                truncated.Add(index);
            planned.Add(new EmbeddingPlannedInput(index, text, wasTruncated));
        }

        var batches = new List<IReadOnlyList<EmbeddingPlannedInput>>();
        for (var offset = 0; offset < planned.Count; offset += maxBatchSize)
            batches.Add(planned.GetRange(offset, Math.Min(maxBatchSize, planned.Count - offset)));

        return new EmbeddingPlan(batches, truncated);
    }

    /// <summary>Truncates to the conservative token budget without splitting surrogate pairs.</summary>
    public static string Truncate(string text, int maxInputTokens, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);
        truncated = false;
        if (text.Length == 0 || maxInputTokens <= 0)
            return text;

        // Keep a little headroom for provider-side tokenizer differences.
        var budget = Math.Max(1d, maxInputTokens * 0.9);
        var builder = new StringBuilder();
        var used = 0d;

        foreach (var rune in text.EnumerateRunes())
        {
            var cost = IsCjk(rune.Value) ? 1d / 1.5d : 1d / 4d;
            if (used + cost > budget)
            {
                truncated = true;
                break;
            }

            builder.Append(rune.ToString());
            used += cost;
        }

        return builder.ToString();
    }

    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cjk = 0;
        var other = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
                cjk++;
            else
                other++;
        }

        return (int)Math.Ceiling(cjk / 1.5d + other / 4d);
    }

    private static bool IsCjk(int codePoint)
        => codePoint is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0x3040 and <= 0x30FF
            or >= 0xAC00 and <= 0xD7AF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F;
}

/// <summary>Result of a host-planned embedding call, including truncation and provider-call evidence.</summary>
