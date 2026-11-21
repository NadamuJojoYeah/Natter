using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Natter.Application.Chat;
using Natter.Chat;

namespace Natter.Feature.Ai;

/// <summary>
/// P5.1 bounded tool loop gateway. The worker owns tool execution and budgets; this part only turns
/// one task step into provider messages, calls the model and parses the single-tool-call protocol.
/// It never writes conversation history or memory.
/// </summary>
public sealed partial class AiChatResponder : ITaskModelGateway
{
    private static readonly Regex ToolCallPattern = new(
        @"<tool_call>\s*(?<json>\{.*?\})\s*</tool_call>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ToolCallTagPattern = new(
        @"</?tool_call\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public async ValueTask<TaskModelReply> CompleteStepAsync(
        TaskModelStep step,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        var messages = BuildTaskStepMessages(step);
        var options = string.IsNullOrWhiteSpace(step.ModelId)
            ? null
            : new ChatOptions { ModelId = step.ModelId };

        try
        {
            using var lease = await _gate.AcquireAsync($"task-loop:{step.TaskId}", cancellationToken).ConfigureAwait(false);
            var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return ParseTaskReply(response.Text);
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

                return TaskModelReply.Call(toolElement.GetString()!, input);
            }
            catch (JsonException)
            {
                return TaskModelReply.Fail("tool_call_invalid", "The tool call is not valid JSON.");
            }
        }

        return TaskModelReply.Answer(StripResponseEnvelope(text));
    }

    private IReadOnlyList<ChatMessage> BuildTaskStepMessages(TaskModelStep step)
    {
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

        foreach (var observation in step.Observations)
            messages.Add(new ChatMessage(ChatRole.User, FormatObservation(observation)));

        return messages;
    }

    private static string FormatObservation(TaskToolObservation observation)
    {
        var status = observation.Ok ? "ok" : "error:" + (observation.ErrorCode ?? "unknown");
        var body = observation.Ok
            ? observation.Output
            : (string.IsNullOrWhiteSpace(observation.ErrorMessage) ? observation.Output : observation.ErrorMessage);
        return $"[tool observation] {observation.Tool} ({status})\n{body}";
    }
}
