using System.Diagnostics;
using System.Text.Json;

namespace Natter.Application.Chat;

/// <summary>
/// Executes a normalized tool call: registry lookup, policy and permission gates, protocol
/// compatibility, JSON schema argument validation, per-tool timeout, output bounding and audit
/// fields. Provider adapters (P5.2b) call this instead of invoking <see cref="ITool"/> directly.
/// </summary>
public sealed class ToolInvoker(IToolRegistry registry, IToolPolicy? policy = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private const int MaxErrorMessageLength = 300;

    private const int MaxReportedSchemaIssues = 5;

    public async ValueTask<ToolCallResult> InvokeAsync(
        ToolCallRequest request,
        RuntimeProfile? profile = null,
        IReadOnlySet<string>? grantedPermissions = null,
        ToolCallProtocol protocol = ToolCallProtocol.Text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);

        var callId = string.IsNullOrWhiteSpace(request.CallId)
            ? "tool:" + Guid.NewGuid().ToString("N")[..12]
            : request.CallId!;
        var startedAt = Stopwatch.GetTimestamp();

        if (!registry.TryGet(request.Tool, out var descriptor))
        {
            return Fail(
                callId,
                request.Tool ?? string.Empty,
                version: string.Empty,
                protocol,
                permissions: [],
                ToolErrorCodes.UnknownTool,
                $"Tool '{request.Tool}' is not registered.");
        }

        if (profile is not null && policy is not null && !policy.IsAllowed(descriptor.Tool, profile))
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                ToolErrorCodes.PolicyDenied,
                $"Tool '{descriptor.Name}' is not allowed by the active tool policy.");
        }

        if (!registry.HasPermissions(descriptor, grantedPermissions))
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                ToolErrorCodes.PermissionDenied,
                $"Tool '{descriptor.Name}' requires permission(s): {string.Join(", ", descriptor.Permissions)}.");
        }

        if (!descriptor.Supports(protocol))
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                ToolErrorCodes.ProtocolUnsupported,
                $"Tool '{descriptor.Name}' does not support the {protocol} tool-call protocol.");
        }

        if (BuildToolRequest(descriptor, request, out var toolRequest, out var validationError) is false)
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                validationError.Code,
                validationError.Message);
        }

        var timeout = descriptor.Metadata.Timeout
                      ?? policy?.Timeout
                      ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
            timeout = DefaultTimeout;

        ToolResult toolResult;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            toolResult = await descriptor.Tool.ExecuteAsync(toolRequest!, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                ToolErrorCodes.Timeout,
                $"Tool '{descriptor.Name}' timed out after {timeout.TotalMilliseconds:0}ms.",
                startedAt);
        }
        catch (Exception ex)
        {
            return Fail(
                callId,
                descriptor.Name,
                descriptor.Metadata.Version,
                protocol,
                descriptor.Permissions,
                ToolErrorCodes.Failed,
                Truncate($"{ex.GetType().Name}: {ex.Message}", MaxErrorMessageLength),
                startedAt);
        }

        var output = toolResult.Output ?? string.Empty;
        var truncated = false;
        var limit = descriptor.Metadata.MaxOutputCharacters;
        if (limit > 0 && output.Length > limit)
        {
            output = output[..limit];
            truncated = true;
        }

        if (toolResult.ErrorCode is { Length: > 0 } errorCode)
        {
            var audit = CreateAudit(
                callId,
                descriptor,
                protocol,
                startedAt,
                output.Length,
                truncated,
                errorCode);
            return new ToolCallResult(
                false,
                output,
                errorCode,
                toolResult.ErrorMessage is null ? null : Truncate(toolResult.ErrorMessage, MaxErrorMessageLength),
                audit);
        }

        return ToolCallResult.Success(
            output,
            CreateAudit(callId, descriptor, protocol, startedAt, output.Length, truncated, null));
    }

    /// <summary>
    /// Validates arguments and maps them onto the legacy <see cref="ToolRequest"/> shape.
    /// Tools without an input schema keep the pre-P5.2 behaviour (raw text input).
    /// </summary>
    private static bool BuildToolRequest(
        ToolDescriptor descriptor,
        ToolCallRequest request,
        out ToolRequest? toolRequest,
        out (string Code, string Message) error)
    {
        toolRequest = null;
        error = (ToolErrorCodes.ArgumentsInvalid, "Tool arguments are invalid.");

        var arguments = request.Arguments;
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            arguments = JsonSerializer.SerializeToElement(new { });

        if (!string.IsNullOrWhiteSpace(descriptor.Metadata.InputSchema))
        {
            if (!ToolInputSchema.TryParse(descriptor.Metadata.InputSchema, out var schema))
            {
                error = (ToolErrorCodes.ArgumentsInvalid, $"Tool '{descriptor.Name}' declares an invalid input schema.");
                return false;
            }

            if (arguments.ValueKind != JsonValueKind.Object)
            {
                error = (ToolErrorCodes.ArgumentsInvalid, "Tool arguments must be a JSON object.");
                return false;
            }

            var issues = ToolInputSchema.Validate(schema, arguments);
            if (issues.Count > 0)
            {
                var reported = issues
                    .Take(MaxReportedSchemaIssues)
                    .Select(issue => $"{issue.Pointer}: {issue.Message}");
                error = (
                    ToolErrorCodes.ArgumentsInvalid,
                    Truncate(string.Join("; ", reported), MaxErrorMessageLength));
                return false;
            }
        }

        var input = request.RawInput;
        if (string.IsNullOrEmpty(input)
            && arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("input", out var inputElement)
            && inputElement.ValueKind == JsonValueKind.String)
        {
            input = inputElement.GetString();
        }

        if (string.IsNullOrEmpty(input))
        {
            if (arguments.ValueKind == JsonValueKind.String)
                input = arguments.GetString();
            else if (arguments.ValueKind == JsonValueKind.Object
                     && arguments.TryGetProperty("text", out var textElement)
                     && textElement.ValueKind == JsonValueKind.String)
                input = textElement.GetString();
        }

        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in arguments.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    mapped[property.Name] = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText();
            }
        }

        if (string.IsNullOrWhiteSpace(descriptor.Metadata.InputSchema) && string.IsNullOrWhiteSpace(input))
        {
            error = (ToolErrorCodes.ArgumentsMissing, $"Tool '{descriptor.Name}' requires an input value.");
            return false;
        }

        toolRequest = new ToolRequest(input ?? string.Empty, mapped.Count == 0 ? null : mapped);
        return true;
    }

    private static ToolCallResult Fail(
        string callId,
        string tool,
        string version,
        ToolCallProtocol protocol,
        IReadOnlyList<string> permissions,
        string code,
        string message,
        long? startedAt = null)
    {
        var audit = new ToolCallAudit(
            callId,
            tool,
            version,
            protocol,
            startedAt is null ? 0 : ElapsedMilliseconds(startedAt.Value),
            0,
            false,
            code,
            permissions);

        return ToolCallResult.Failure(code, message, audit);
    }

    private static ToolCallAudit CreateAudit(
        string callId,
        ToolDescriptor descriptor,
        ToolCallProtocol protocol,
        long startedAt,
        int outputCharacters,
        bool truncated,
        string? errorCode)
        => new(
            callId,
            descriptor.Name,
            descriptor.Metadata.Version,
            protocol,
            ElapsedMilliseconds(startedAt),
            outputCharacters,
            truncated,
            errorCode,
            descriptor.Permissions);

    private static long ElapsedMilliseconds(long startedAt)
        => (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "…";
}
