namespace Natter.Application.Chat;

/// <summary>Small read-only HTTP tool with an explicit host allowlist and response limit.</summary>
public sealed class HttpFetchTool(
    HttpClient httpClient,
    IReadOnlySet<string>? allowedHosts = null,
    int maxBytes = 64 * 1024) : ITool
{
    private readonly HttpClient _http = httpClient;
    private readonly IReadOnlySet<string> _allowed = allowedHosts ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxBytes = Math.Clamp(maxBytes, 1, 256 * 1024);

    public string Name => "http-fetch";
    public string Description => "Fetches a read-only HTTPS URL from the configured host allowlist.";
    public ToolCapabilities Capabilities => ToolCapabilities.ReadOnly | ToolCapabilities.Network;

    public async ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Input, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return new ToolResult("", "invalid_url", "Only absolute HTTPS URLs are allowed.");
        if (!_allowed.Contains(uri.Host))
            return new ToolResult("", "host_not_allowed", "Host is not in the tool allowlist.");

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ToolResult("", "http_error", $"HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[Math.Min(_maxBytes, 16 * 1024)];
            using var output = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var take = Math.Min(read, _maxBytes - (int)output.Length);
                if (take > 0)
                    output.Write(buffer, 0, take);
                if (output.Length >= _maxBytes)
                    break;
            }
            return new ToolResult(System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult("", "http_failed", ex.GetType().Name);
        }
    }
}
