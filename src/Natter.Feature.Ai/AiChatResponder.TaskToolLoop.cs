using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>
/// P5.x bounded tool loop gateway. The worker owns tool execution, budgets and observation
/// sanitising; this part only turns one task step into provider messages, calls the model and
/// returns either the final text or one tool call. Native function calling is used when the
/// provider declares support, otherwise the text protocol is used. It never writes conversation
/// history or memory.
/// </summary>
public sealed partial class AiChatResponder : ITaskModelGateway
{
    /// <summary>Declared by the provider adapter; defaults to the text protocol.</summary>
    public ToolProtocolCapabilities ProtocolCapabilities { get; }

    private static readonly Regex ToolCallPattern = new(
        @"<tool_call>\s*(?<json>\{.*?\})\s*</tool_call>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ToolCallTagPattern = new(
        @"</?tool_call\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly JsonElement DefaultToolSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}""").RootElement.Clone();

    public async ValueTask<TaskModelReply> CompleteStepAsync(
        TaskModelStep step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        var messages = BuildTaskStepMessages(step);
        var options = BuildTaskOptions(step);

        try
        {
            using var lease = await _gate.AcquireAsync($"task-loop:{step.TaskId}", cancellationToken).ConfigureAwait(false);
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return step.Protocol == ToolCallProtocol.Native
                ? ParseNativeReply(response)
                : ParseTaskReply(response.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TaskModelReply.Fail(
                TaskToolLoopCodes.ModelStepTimeout,
                "The AI provider did not answer within the task step budget.");
        }
        catch (Exception ex) when (ex is IAiProviderException provider)
        {
            return TaskModelReply.Fail(
                provider.Kind == AiErrorKind.Timeout
                    ? TaskToolLoopCodes.ModelStepTimeout
                    : TaskToolLoopCodes.ModelStepFailed,
                $"AI provider error: {provider.Kind} (status {provider.StatusCode}, code {provider.ErrorCode}).");
        }
        catch (Exception ex)
        {
            return TaskModelReply.Fail(
                TaskToolLoopCodes.ModelStepFailed,
                $"AI request failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Parses one model step: a single <c>&lt;tool_call&gt;{json}&lt;/tool_call&gt;</c> becomes a tool
    /// request, a malformed tag becomes an explicit error, anything else is the final answer with
    /// the provider wrapper tags stripped.
    /// </summary>
    internal static TaskModelReply ParseTaskReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TaskModelReply.Answer(string.Empty);

        if (ToolCallTagPattern.IsMatch(text))
        {
            var match = ToolCallPattern.Match(text);
            if (!match.Success)
            {
                return TaskModelReply.Fail(
                    "tool_call_invalid",
                    "The model returned a malformed tool call; expected <tool_call>{\"tool\":\"…\",\"input\":\"…\"}</tool_call>.");
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups["json"].Value);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("tool", out var toolElement)
                    || toolElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(toolElement.GetString()))
                {
                    return TaskModelReply.Fail("tool_call_invalid", "The tool call is missing a tool name.");
                }

                var input = root.TryGetProperty("input", out var inputElement)
                            && inputElement.ValueKind == JsonValueKind.String
                    ? inputElement.GetString() ?? string.Empty
                    : string.Empty;

                // The text protocol envelope is not the tool argument object; the worker derives
                // arguments from <input> (or the raw input) for schema validation.
                return TaskModelReply.Call(toolElement.GetString()!, input);
            }
            catch (JsonException)
            {
                return TaskModelReply.Fail("tool_call_invalid", "The tool call is not valid JSON.");
            }
        }

        return TaskModelReply.Answer(StripResponseEnvelope(text));
    }

    /// <summary>Parses a native <see cref="FunctionCallContent"/>; text-only replies are final.</summary>
    internal static TaskModelReply ParseNativeReply(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var message in response.Messages ?? [])
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is not FunctionCallContent call || string.IsNullOrWhiteSpace(call.Name))
                    continue;

                var arguments = call.Arguments is null
                    ? JsonSerializer.SerializeToElement(new { input = string.Empty })
                    : JsonSerializer.SerializeToElement(call.Arguments);

                var input = arguments.ValueKind == JsonValueKind.Object
                            && arguments.TryGetProperty("input", out var inputElement)
                            && inputElement.ValueKind == JsonValueKind.String
                    ? inputElement.GetString() ?? string.Empty
                    : arguments.GetRawText();

                return TaskModelReply.Call(call.Name, input, arguments);
            }
        }

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = string.Concat(
                (response.Messages ?? [])
                .SelectMany(message => message.Contents ?? [])
                .OfType<TextContent>()
                .Select(content => content.Text));
        }

        return TaskModelReply.Answer(StripResponseEnvelope(text));
    }

    private ChatOptions? BuildTaskOptions(TaskModelStep step)
    {
        var hasModel = !string.IsNullOrWhiteSpace(step.ModelId);
        if (step.Protocol != ToolCallProtocol.Native || step.Tools.Count == 0)
            return hasModel ? new ChatOptions { ModelId = step.ModelId } : null;

        var options = new ChatOptions
        {
            ModelId = step.ModelId,
            AllowMultipleToolCalls = false,
            ToolMode = ChatToolMode.Auto,
        };

        options.Tools = step.Tools
            .Select(tool => (AITool)CreateToolDeclaration(tool))
            .ToList();
        return options;
    }

    private static AIFunctionDeclaration CreateToolDeclaration(TaskToolDescriptor tool)
    {
        var schema = ToolInputSchema.TryParse(tool.InputSchema, out var parsed)
            ? parsed
            : DefaultToolSchema;
        return AIFunctionFactory.CreateDeclaration(
            tool.Name,
            tool.Description,
            schema);
    }

    private IReadOnlyList<ChatMessage> BuildTaskStepMessages(TaskModelStep step)
    {
        var native = step.Protocol == ToolCallProtocol.Native;
        var system = new StringBuilder();
        system.AppendLine(string.IsNullOrWhiteSpace(_options.SystemPrompt)
            ? "You are Natter, a concise assistant."
            : _options.SystemPrompt);
        system.AppendLine();
        system.AppendLine("This is a background task. Tool calls are bounded and must be read-only.");

        if (step.Tools.Count == 0)
        {
            system.AppendLine("No tools are available; answer the user directly.");
        }
        else if (native)
        {
            system.AppendLine("Use the provided tools through the native tool interface when they help.");
            system.AppendLine("When you have enough information, answer the user directly without calling a tool.");
        }
        else
        {
            system.AppendLine("Available tools:");
            foreach (var tool in step.Tools)
                system.AppendLine($"- {tool.Name}: {tool.Description}");

            system.AppendLine("To call one tool, reply with exactly this tag and no other text:");
            system.AppendLine("<tool_call>{\"tool\":\"<tool-name>\",\"input\":\"<input>\"}</tool_call>");
            system.AppendLine("When you have enough information, answer the user directly. Never include a tool_call tag in the final answer.");
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system.ToString().TrimEnd()),
            new(ChatRole.User, step.Prompt),
        };

        for (var index = 0; index < step.Observations.Count; index++)
        {
            var observation = step.Observations[index];
            if (native)
            {
                // Reconstruct the assistant call / tool result pair so native providers see a
                // well-formed tool conversation without touching host history.
                var callId = $"task-observation-{index}";
                var arguments = new Dictionary<string, object?> { ["input"] = observation.Input };
                messages.Add(new ChatMessage(
                    ChatRole.Assistant,
                    new List<AIContent> { new FunctionCallContent(callId, observation.Tool, arguments) }));
                messages.Add(new ChatMessage(
                    ChatRole.Tool,
                    new List<AIContent> { new FunctionResultContent(callId, FormatObservationBody(observation)) }));
            }
            else
            {
                messages.Add(new ChatMessage(ChatRole.User, FormatObservation(observation)));
            }
        }

        return messages;
    }

    private static string FormatObservation(TaskToolObservation observation)
        => $"[tool observation] {observation.Tool} ({Status(observation)})\n{FormatObservationBody(observation)}";

    private static string FormatObservationBody(TaskToolObservation observation)
        => observation.Ok
            ? observation.Output
            : $"{observation.ErrorCode ?? "tool_error"}: "
              + (string.IsNullOrWhiteSpace(observation.ErrorMessage) ? observation.Output : observation.ErrorMessage);

    private static string Status(TaskToolObservation observation)
        => observation.Ok ? "ok" : "error:" + (observation.ErrorCode ?? "unknown");
}
