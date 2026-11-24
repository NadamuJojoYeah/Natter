using System.Text.Json;

namespace Natter.Application.Chat;

/// <summary>
/// Hard limits for the P5.1 bounded tool loop. The effective number of tool calls per task is
/// <c>min(TaskExecutionContext.MaxToolCalls, MaxToolCallsHardCap)</c>.
/// </summary>
public sealed class ToolLoopOptions
{
    /// <summary>Absolute cap for tool calls per task (default 3, the P5.1 contract).</summary>
    public int MaxToolCallsHardCap { get; set; } = 3;

    /// <summary>Cap for model round-trips, including the final answer step.</summary>
    public int MaxModelSteps { get; set; } = 5;

    /// <summary>Maximum characters of one tool observation before it is sent back to the model.</summary>
    public int MaxObservationCharacters { get; set; } = 2_000;
}

/// <summary>Tool description exposed to the model inside a task tool loop.</summary>
public sealed record TaskToolDescriptor(
    string Name,
    string Description,
    string? InputSchema = null,
    string Version = "1.0.0",
    bool NativeCallSupported = false);

/// <summary>One tool result (already redacted/truncated) fed back to the model as an observation.</summary>
public sealed record TaskToolObservation(
    string Tool,
    string Input,
    bool Ok,
    string Output,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>One model step input: task prompt, available tools and previous observations.</summary>
public sealed record TaskModelStep(
    string TaskId,
    string Prompt,
    string? ModelId,
    IReadOnlyList<TaskToolDescriptor> Tools,
    IReadOnlyList<TaskToolObservation> Observations,
    int StepIndex,
    ToolCallProtocol Protocol = ToolCallProtocol.Text);

/// <summary>
/// One tool invocation requested by the model. <see cref="Arguments"/> carries structured native
/// arguments; text-protocol calls leave it null and rely on <see cref="Input"/>.
/// </summary>
public sealed record TaskToolCall(string Tool, string Input, JsonElement? Arguments = null);

/// <summary>Model reply for one step: a final text answer, a tool call, or an explicit error.</summary>
public sealed record TaskModelReply(
    string? Text,
    TaskToolCall? ToolCall = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Ok => ErrorCode is null;

    public static TaskModelReply Answer(string? text) => new(text);

    public static TaskModelReply Call(string tool, string input, JsonElement? arguments = null)
        => new(null, new TaskToolCall(tool, input, arguments));

    public static TaskModelReply Fail(string code, string message) => new(null, null, code, message);
}

/// <summary>
/// Provider-neutral gateway for the bounded task tool loop. Implemented by the shared
/// <c>AiChatResponder</c> so the loop works for every provider without plugin changes; it never
/// writes conversation history or memory.
/// </summary>
public interface ITaskModelGateway
{
    /// <summary>
    /// Tool-call transports the routed provider supports. Legacy gateways default to the text
    /// protocol so existing implementations keep working; native-capable providers override it.
    /// </summary>
    ToolProtocolCapabilities ProtocolCapabilities => ToolProtocolCapabilities.TextOnly;

    ValueTask<TaskModelReply> CompleteStepAsync(
        TaskModelStep step,
        CancellationToken cancellationToken = default);
}

/// <summary>Stable error codes returned by the bounded tool loop.</summary>
public static class TaskToolLoopCodes
{
    public const string ToolBudgetExceeded = "tool_budget_exceeded";
    public const string ToolNotAllowed = "tool_not_allowed";
    public const string ToolTimeout = "tool_timeout";
    public const string ToolFailed = "tool_failed";
    public const string ModelStepTimeout = "ai_step_timeout";
    public const string ModelStepFailed = "ai_step_failed";
    public const string ToolLoopFailed = "tool_loop_failed";
    public const string ProtocolUnsupported = "tool_protocol_unsupported";
    public const string EmptyResponse = "empty_response";
}
