namespace Natter.Application.Chat;

public sealed class ClockTool(TimeProvider? timeProvider = null) : ITool
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Name => "clock";
    public string Description => "Returns the current UTC time.";
    public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly;

    public ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ToolResult(_time.GetUtcNow().ToString("O")));
}
