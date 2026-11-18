namespace Natter.Application.Chat;

public sealed class TextTransformTool : ITool
{
    public string Name => "text-transform";
    public string Description => "Applies a deterministic uppercase/length transform to text.";
    public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

    public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ToolResult($"{request.Input.ToUpperInvariant()} (chars={request.Input.Length})"));
}
