namespace Natter.Application.Chat;

/// <summary>
/// P5.2 tool metadata. Every property has a safe default so existing <see cref="ITool"/>
/// implementations stay source- and binary-compatible (the <see cref="ITool.Metadata"/> default
/// member returns <see cref="Legacy"/>).
/// </summary>
public sealed record ToolMetadata(
    string Version = "1.0.0",
    string? InputSchema = null,
    IReadOnlyList<string>? Permissions = null,
    int MaxOutputCharacters = 0,
    TimeSpan? Timeout = null,
    bool NativeCallSupported = false)
{
    /// <summary>Metadata for tools written before P5.2: no schema, no output limit, text protocol only.</summary>
    public static ToolMetadata Legacy { get; } = new();
}

/// <summary>How a tool call can be transported between the host and a model provider.</summary>
public enum ToolCallProtocol
{
    /// <summary>Provider-neutral text protocol (<c>&lt;tool_call&gt;{json}&lt;/tool_call&gt;</c> in P5.1).</summary>
    Text = 0,

    /// <summary>Provider-native structured function/tool calling.</summary>
    Native = 1,
}

/// <summary>What the routed provider can do; negotiated against the tool registry.</summary>
public sealed record ToolProtocolCapabilities(bool NativeFunctionCalling, bool TextProtocol)
{
    public static ToolProtocolCapabilities TextOnly { get; } = new(false, true);

    public static ToolProtocolCapabilities NativeOnly { get; } = new(true, false);

    public static ToolProtocolCapabilities NativeAndText { get; } = new(true, true);
}

/// <summary>Stable tool-call error codes shared by the registry, invoker and audit trail.</summary>
public static class ToolErrorCodes
{
    public const string UnknownTool = "tool_unknown";
    public const string ArgumentsMissing = "tool_arguments_missing";
    public const string ArgumentsInvalid = "tool_arguments_invalid";
    public const string PermissionDenied = "tool_permission_denied";
    public const string PolicyDenied = "tool_policy_denied";
    public const string ProtocolUnsupported = "tool_protocol_unsupported";
    public const string Timeout = "tool_timeout";
    public const string Failed = "tool_failed";
    public const string Succeeded = "tool_ok";
}

/// <summary>One normalized tool call request. <see cref="Arguments"/> is the canonical JSON object.</summary>
public sealed record ToolCallRequest(
    string Tool,
    System.Text.Json.JsonElement Arguments,
    string? CallId = null,
    string? RawInput = null)
{
    public static ToolCallRequest FromText(string tool, string? input, string? callId = null)
        => new(
            tool,
            System.Text.Json.JsonSerializer.SerializeToElement(new { input = input ?? string.Empty }),
            callId,
            input);
}

/// <summary>Audit fields recorded for every tool invocation (no prompt/secret content).</summary>
public sealed record ToolCallAudit(
    string CallId,
    string Tool,
    string Version,
    ToolCallProtocol Protocol,
    long DurationMs,
    int OutputCharacters,
    bool Truncated,
    string? ErrorCode,
    IReadOnlyList<string> Permissions);

/// <summary>Normalized tool call result.</summary>
public sealed record ToolCallResult(
    bool Ok,
    string Output,
    string? ErrorCode,
    string? ErrorMessage,
    ToolCallAudit Audit)
{
    public static ToolCallResult Success(string output, ToolCallAudit audit) => new(true, output, null, null, audit);

    public static ToolCallResult Failure(string code, string message, ToolCallAudit audit)
        => new(false, string.Empty, code, message, audit);
}
