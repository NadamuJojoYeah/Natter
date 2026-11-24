using System.Text.Json;
using Natter.Application.Chat;
using Natter.Chat;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// P5.2a tool registry: metadata, descriptor/capability negotiation, schema validation, policy and
/// permission gates, timeouts, output bounds and audit fields — with legacy tool compatibility.
/// </summary>
public sealed class P5ToolRegistryTests
{
    private const string EchoSchema = """
        {
          "type": "object",
          "properties": {
            "input": { "type": "string", "minLength": 1 },
            "repeat": { "type": "integer", "minimum": 1, "maximum": 3 }
          },
          "required": ["input"],
          "additionalProperties": false
        }
        """;

    [Fact]
    public void Registry_indexes_tools_and_deduplicates_names()
    {
        var first = new FakeTool("echo", ToolCapabilities.ReadOnly);
        var duplicate = new FakeTool("echo", ToolCapabilities.ReadOnly);
        var clock = new FakeTool("clock", ToolCapabilities.ReadOnly);

        var registry = new ToolRegistry([first, duplicate, clock]);

        Assert.Equal(["clock", "echo"], registry.Descriptors.Select(descriptor => descriptor.Name));
        Assert.Equal(["echo"], registry.DuplicateNames);
        Assert.Same(first, registry.Descriptors.Single(descriptor => descriptor.Name == "echo").Tool);
    }

    [Fact]
    public async Task Legacy_tool_keeps_working_through_the_registry()
    {
        var tool = new FakeTool("echo", ToolCapabilities.ReadOnly, (request, _) =>
            ValueTask.FromResult(new ToolResult(request.Input)));
        var registry = new ToolRegistry([tool]);

        var result = await new ToolInvoker(registry).InvokeAsync(
            ToolCallRequest.FromText("echo", "hello", "call-1"));

        Assert.True(result.Ok);
        Assert.Equal("hello", result.Output);
        Assert.Equal("1.0.0", result.Audit.Version);
        Assert.Equal("call-1", result.Audit.CallId);
        Assert.Equal(ToolCallProtocol.Text, result.Audit.Protocol);
        Assert.Empty(result.Audit.Permissions);
        Assert.Equal(5, result.Audit.OutputCharacters);
        Assert.False(result.Audit.Truncated);
        Assert.Equal(1, tool.Calls);
        Assert.Equal("hello", tool.LastInput);
    }

    [Fact]
    public async Task Schema_validated_tool_receives_mapped_arguments_and_audit_metadata()
    {
        var tool = new FakeTool(
            "echo",
            ToolCapabilities.ReadOnly,
            (request, _) => ValueTask.FromResult(new ToolResult(request.Input)),
            new ToolMetadata(
                Version: "2.1.0",
                InputSchema: EchoSchema,
                Permissions: ["echo.read"],
                MaxOutputCharacters: 100,
                Timeout: TimeSpan.FromSeconds(5),
                NativeCallSupported: true));
        var registry = new ToolRegistry([tool]);
        var request = new ToolCallRequest(
            "echo",
            JsonSerializer.SerializeToElement(new { input = "hi", repeat = 2 }),
            "call-2");

        var result = await new ToolInvoker(registry).InvokeAsync(
            request,
            grantedPermissions: new HashSet<string>(["echo.read"], StringComparer.Ordinal));

        Assert.True(result.Ok);
        Assert.Equal("hi", tool.LastInput);
        Assert.Equal("2", tool.LastArguments!["repeat"]);
        Assert.Equal("2.1.0", result.Audit.Version);
        Assert.Equal(["echo.read"], result.Audit.Permissions);
        Assert.Equal(ToolErrorCodes.Succeeded, result.Audit.ErrorCode is null ? ToolErrorCodes.Succeeded : "unexpected");
    }

    [Theory]
    [InlineData("""{ "repeat": 2 }""", "/input")]
    [InlineData("""{ "input": "x", "truthy": 1 }""", "/truthy")]
    [InlineData("""{ "input": 5 }""", "/input")]
    [InlineData("""{ "input": "x", "repeat": 0 }""", "/repeat")]
    [InlineData("""{ "input": "x", "repeat": 4 }""", "/repeat")]
    public async Task Invalid_arguments_are_rejected_before_execution(string argumentsJson, string pointer)
    {
        var tool = new FakeTool(
            "echo",
            ToolCapabilities.ReadOnly,
            (request, _) => ValueTask.FromResult(new ToolResult(request.Input)),
            new ToolMetadata(InputSchema: EchoSchema));
        var registry = new ToolRegistry([tool]);
        var request = new ToolCallRequest("echo", JsonDocument.Parse(argumentsJson).RootElement.Clone(), "call-3");

        var result = await new ToolInvoker(registry).InvokeAsync(request);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.ArgumentsInvalid, result.ErrorCode);
        Assert.Contains(pointer, result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(0, tool.Calls);
        Assert.Equal(ToolErrorCodes.ArgumentsInvalid, result.Audit.ErrorCode);
    }

    [Fact]
    public async Task Unknown_tool_returns_an_explicit_error()
    {
        var registry = new ToolRegistry([new FakeTool("clock", ToolCapabilities.ReadOnly)]);

        var result = await new ToolInvoker(registry).InvokeAsync(ToolCallRequest.FromText("missing", "x", "call-4"));

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.UnknownTool, result.ErrorCode);
        Assert.Equal("call-4", result.Audit.CallId);
        Assert.Equal("missing", result.Audit.Tool);
    }

    [Fact]
    public async Task Policy_denied_tool_is_not_executed()
    {
        var shell = new FakeTool("shell", ToolCapabilities.ReadOnly | ToolCapabilities.Shell);
        var registry = new ToolRegistry([shell]);
        var profile = CreateProfile(allowNetwork: false);

        var result = await new ToolInvoker(registry, new ReadOnlyToolPolicy())
            .InvokeAsync(ToolCallRequest.FromText("shell", "ls"), profile);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.PolicyDenied, result.ErrorCode);
        Assert.Equal(0, shell.Calls);
    }

    [Fact]
    public async Task Permission_gate_requires_declared_permissions()
    {
        var tool = new FakeTool(
            "http",
            ToolCapabilities.ReadOnly,
            (request, _) => ValueTask.FromResult(new ToolResult("ok")),
            new ToolMetadata(Permissions: ["network.read"]));
        var registry = new ToolRegistry([tool]);

        var denied = await new ToolInvoker(registry).InvokeAsync(
            ToolCallRequest.FromText("http", "https://example.com"),
            grantedPermissions: new HashSet<string>(StringComparer.Ordinal));
        Assert.False(denied.Ok);
        Assert.Equal(ToolErrorCodes.PermissionDenied, denied.ErrorCode);
        Assert.Equal(0, tool.Calls);

        var allowed = await new ToolInvoker(registry).InvokeAsync(
            ToolCallRequest.FromText("http", "https://example.com"),
            grantedPermissions: new HashSet<string>(["network.read"], StringComparer.Ordinal));

        Assert.True(allowed.Ok);
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task Tool_timeout_is_reported()
    {
        var slow = new FakeTool(
            "slow",
            ToolCapabilities.ReadOnly,
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ToolResult("never");
            },
            new ToolMetadata(Timeout: TimeSpan.FromMilliseconds(40)));
        var registry = new ToolRegistry([slow]);

        var result = await new ToolInvoker(registry).InvokeAsync(ToolCallRequest.FromText("slow", "wait"));

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.Timeout, result.ErrorCode);
        Assert.Contains("40ms", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_limit_truncates_and_marks_the_result()
    {
        var noisy = new FakeTool(
            "noisy",
            ToolCapabilities.ReadOnly,
            (_, _) => ValueTask.FromResult(new ToolResult(new string('x', 50))),
            new ToolMetadata(MaxOutputCharacters: 10));
        var registry = new ToolRegistry([noisy]);

        var result = await new ToolInvoker(registry).InvokeAsync(ToolCallRequest.FromText("noisy", "dump"));

        Assert.True(result.Ok);
        Assert.Equal(10, result.Output.Length);
        Assert.True(result.Audit.Truncated);
        Assert.Equal(10, result.Audit.OutputCharacters);
    }

    [Fact]
    public async Task Tool_exception_becomes_a_structured_failure()
    {
        var broken = new FakeTool(
            "broken",
            ToolCapabilities.ReadOnly,
            (_, _) => throw new InvalidOperationException("boom"));
        var registry = new ToolRegistry([broken]);

        var result = await new ToolInvoker(registry).InvokeAsync(ToolCallRequest.FromText("broken", "x"));

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.Failed, result.ErrorCode);
        Assert.Contains("boom", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Equal(ToolErrorCodes.Failed, result.Audit.ErrorCode);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var slow = new FakeTool(
            "slow",
            ToolCapabilities.ReadOnly,
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ToolResult("never");
            });
        var registry = new ToolRegistry([slow]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await new ToolInvoker(registry).InvokeAsync(
                ToolCallRequest.FromText("slow", "wait"),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Protocol_negotiation_supports_native_and_text_fallbacks()
    {
        var nativeTool = new FakeTool("native", ToolCapabilities.ReadOnly, metadata: new ToolMetadata(NativeCallSupported: true));
        var legacyTool = new FakeTool("legacy", ToolCapabilities.ReadOnly);

        var nativeRegistry = new ToolRegistry([nativeTool], ToolProtocolCapabilities.NativeAndText);
        var legacyRegistry = new ToolRegistry([legacyTool], ToolProtocolCapabilities.NativeAndText);
        var textOnlyRegistry = new ToolRegistry([nativeTool], ToolProtocolCapabilities.TextOnly);
        var nativeOnlyWithLegacy = new ToolRegistry([legacyTool], ToolProtocolCapabilities.NativeOnly);
        var nativeOnlyNative = new ToolRegistry([nativeTool], ToolProtocolCapabilities.NativeOnly);

        Assert.Equal(ToolCallProtocol.Native, nativeRegistry.Negotiate().Protocol);
        Assert.Equal(ToolCallProtocol.Text, legacyRegistry.Negotiate().Protocol);
        Assert.Equal(ToolCallProtocol.Text, textOnlyRegistry.Negotiate().Protocol);
        Assert.Equal(ToolCallProtocol.Native, nativeOnlyNative.Negotiate().Protocol);

        var unsupported = nativeOnlyWithLegacy.Negotiate();
        Assert.False(unsupported.Ok);
        Assert.Equal(ToolErrorCodes.ProtocolUnsupported, unsupported.Code);
    }

    [Fact]
    public async Task Native_only_provider_rejects_text_only_tools_at_invoke_time()
    {
        var legacy = new FakeTool("legacy", ToolCapabilities.ReadOnly);
        var registry = new ToolRegistry([legacy], ToolProtocolCapabilities.NativeOnly);

        var result = await new ToolInvoker(registry).InvokeAsync(
            ToolCallRequest.FromText("legacy", "x"),
            protocol: ToolCallProtocol.Native);

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.ProtocolUnsupported, result.ErrorCode);
        Assert.Equal(0, legacy.Calls);
    }

    [Fact]
    public void Allowed_filters_by_policy_and_permissions()
    {
        var readonlyTool = new FakeTool("readonly", ToolCapabilities.ReadOnly);
        var shell = new FakeTool("shell", ToolCapabilities.ReadOnly | ToolCapabilities.Shell);
        var privileged = new FakeTool(
            "privileged",
            ToolCapabilities.ReadOnly,
            metadata: new ToolMetadata(Permissions: ["storage"]));
        var registry = new ToolRegistry([readonlyTool, shell, privileged]);
        var profile = CreateProfile(allowNetwork: false);

        var allowed = registry.Allowed(profile, new ReadOnlyToolPolicy(), new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(["readonly"], allowed.Select(descriptor => descriptor.Name));
        Assert.Contains(registry.Descriptors, descriptor => descriptor.Name == "privileged");
    }

    [Fact]
    public void Descriptor_projects_to_the_p5_1_task_contract()
    {
        var registry = new ToolRegistry([new FakeTool("clock", ToolCapabilities.ReadOnly)]);
        var descriptor = registry.Descriptors.Single();

        var taskDescriptor = descriptor.ToTaskToolDescriptor();

        Assert.Equal(descriptor.Name, taskDescriptor.Name);
        Assert.Equal(descriptor.Description, taskDescriptor.Description);
    }

    [Theory]
    [InlineData("""{ "type": "object", "properties": { "a": { "type": "string" } } }""", """{ "a": 1 }""", "schema.type")]
    [InlineData("""{ "type": "string", "enum": ["red", "green"] }""", "\"blue\"", "schema.enum")]
    [InlineData("""{ "type": "number", "minimum": 1, "maximum": 3 }""", "4", "schema.maximum")]
    [InlineData("""{ "type": "string", "minLength": 2, "maxLength": 4 }""", "\"x\"", "schema.minLength")]
    [InlineData("""{ "type": "string", "pattern": "^[a-z]+$" }""", "\"ABC\"", "schema.pattern")]
    [InlineData("""{ "type": "array", "items": { "type": "integer" } }""", "[1, \"two\"]", "schema.type")]
    [InlineData("""{ "type": "object", "properties": { "a": { "type": "string" } }, "additionalProperties": false }""", """{ "a": "x", "b": 1 }""", "schema.additional")]
    public void Schema_validator_covers_the_supported_subset(string schemaJson, string valueJson, string expectedCode)
    {
        var schema = JsonDocument.Parse(schemaJson).RootElement.Clone();
        var value = JsonDocument.Parse(valueJson).RootElement.Clone();

        var issues = ToolInputSchema.Validate(schema, value);

        Assert.Contains(issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public async Task Invalid_tool_schema_is_reported_without_running_the_tool()
    {
        var tool = new FakeTool(
            "broken-schema",
            ToolCapabilities.ReadOnly,
            (_, _) => ValueTask.FromResult(new ToolResult("never")),
            new ToolMetadata(InputSchema: "{ not json"));
        var registry = new ToolRegistry([tool]);

        var result = await new ToolInvoker(registry).InvokeAsync(
            new ToolCallRequest("broken-schema", JsonSerializer.SerializeToElement(new { input = "x" })));

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.ArgumentsInvalid, result.ErrorCode);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Missing_legacy_input_is_reported_as_arguments_missing()
    {
        var tool = new FakeTool("echo", ToolCapabilities.ReadOnly);
        var registry = new ToolRegistry([tool]);

        var result = await new ToolInvoker(registry).InvokeAsync(
            new ToolCallRequest("echo", JsonSerializer.SerializeToElement(new { })));

        Assert.False(result.Ok);
        Assert.Equal(ToolErrorCodes.ArgumentsMissing, result.ErrorCode);
        Assert.Equal(0, tool.Calls);
    }

    private static RuntimeProfile CreateProfile(bool allowNetwork)
        => new()
        {
            Id = "p",
            Ai = new ProfileAi { Provider = "test#probe" },
            Policy = new ProfilePolicy { AllowNetworkTools = allowNetwork },
        };

    private sealed class FakeTool(
        string name,
        ToolCapabilities capabilities,
        Func<ToolRequest, CancellationToken, ValueTask<ToolResult>>? execute = null,
        ToolMetadata? metadata = null) : ITool
    {
        private int _calls;

        public string Name { get; } = name;

        public string Description => Name + " test tool";

        public ToolCapabilities Capabilities { get; } = capabilities;

        public ToolMetadata Metadata => metadata ?? ToolMetadata.Legacy;

        public int Calls => Volatile.Read(ref _calls);

        public string? LastInput { get; private set; }

        public IReadOnlyDictionary<string, string>? LastArguments { get; private set; }

        public async ValueTask<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            LastInput = request.Input;
            LastArguments = request.Arguments;
            return execute is null
                ? new ToolResult(request.Input)
                : await execute(request, cancellationToken);
        }
    }
}
