using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Natter.Application.Chat;

/// <summary>Trace event kinds written by the task runtime.</summary>
public static class TaskTraceKinds
{
    public const string Started = "started";
    public const string Step = "step";
    public const string Tool = "tool";
    public const string Final = "final";
}

/// <summary>
/// Whitelisted trace payload writer. Only ids, names, codes, numbers and booleans are accepted;
/// prompt text, tool arguments, tool output bodies, secrets and free-form messages are dropped.
/// </summary>
public static class TaskTracePayload
{
    public const int MaxStringLength = 128;

    private static readonly Regex SafeKey = new(
        "^[a-z][a-zA-Z0-9]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> DeniedKeys = new(StringComparer.Ordinal)
    {
        "prompt",
        "text",
        "input",
        "output",
        "args",
        "arguments",
        "result",
        "resulttext",
        "message",
        "errormessage",
        "secret",
        "token",
        "apikey",
        "authorization",
        "payload",
        "body",
    };

    private static readonly string[] DeniedPrefixes = ["sk-", "ak-", "bearer"];

    public static string Build(params (string Key, object? Value)[] values)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (Normalize(key, value) is { } node)
                obj[key] = node;
        }

        return obj.ToJsonString();
    }

    /// <summary>Rejects payloads that violate the trace whitelist (stores call this defensively).</summary>
    public static void EnsureSafe(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Task trace payload must be valid JSON.", nameof(payloadJson), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Task trace payload must be a JSON object.", nameof(payloadJson));

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IsSafeKey(property.Name))
                    throw new ArgumentException($"Task trace key '{property.Name}' is not allowed.", nameof(payloadJson));

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                    case JsonValueKind.Number:
                        break;
                    case JsonValueKind.String:
                        if (!IsSafeToken(property.Value.GetString()))
                            throw new ArgumentException($"Task trace value for '{property.Name}' is not an id/name/code.", nameof(payloadJson));
                        break;
                    default:
                        throw new ArgumentException($"Task trace value for '{property.Name}' must be a scalar.", nameof(payloadJson));
                }
            }
        }
    }

    public static bool IsSafeKey(string? key)
        => !string.IsNullOrEmpty(key)
           && SafeKey.IsMatch(key)
           && !DeniedKeys.Contains(key);

    public static bool IsSafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxStringLength)
            return false;

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/' or '#'))
                return false;
        }

        foreach (var prefix in DeniedPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static JsonNode? Normalize(string key, object? value)
    {
        if (!IsSafeKey(key))
            return null;

        return value switch
        {
            null => null,
            bool boolean => JsonValue.Create(boolean),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number when double.IsFinite(number) => JsonValue.Create(number),
            string text when IsSafeToken(text) => JsonValue.Create(text),
            _ => null,
        };
    }
}

/// <summary>Minimal trace view for /status and /tasks (kind/time/name/code only).</summary>
public sealed record TaskTraceEntry(
    string Kind,
    DateTimeOffset At,
    string? Name = null,
    string? Code = null,
    int? Step = null,
    string? Status = null)
{
    public static TaskTraceEntry From(TaskEvent taskEvent)
    {
        ArgumentNullException.ThrowIfNull(taskEvent);
        string? name = null;
        string? code = null;
        int? step = null;
        string? status = null;

        if (!string.IsNullOrWhiteSpace(taskEvent.PayloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(taskEvent.PayloadJson);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                        name = nameElement.GetString();
                    if (root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                        code = codeElement.GetString();
                    if (root.TryGetProperty("step", out var stepElement) && stepElement.TryGetInt32(out var stepNumber))
                        step = stepNumber;
                    if (root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
                        status = statusElement.GetString();
                }
            }
            catch (JsonException)
            {
                // A malformed legacy payload is ignored; /status degrades gracefully.
            }
        }

        return new TaskTraceEntry(taskEvent.Kind, taskEvent.CreatedAt, name, code, step, status);
    }
}
