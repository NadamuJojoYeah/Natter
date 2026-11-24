using System.Net;

namespace Natter.Infrastructure.Chat;

/// <summary>
/// Configurable settings for the shared AI-provider HTTP client (A21). The old registration was a
/// bare singleton <see cref="HttpClient"/>: its handler never recycled connections, so DNS changes
/// were invisible, and there was no way to tune proxy/timeout behaviour.
/// </summary>
public sealed class NatterHttpClientOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often pooled connections are recycled; keeps DNS changes visible.</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether the shared client honours HTTP(S)_PROXY / a configured proxy URL.</summary>
    public bool UseProxy { get; init; } = true;

    public string? ProxyUrl { get; init; }

    public TimeSpan? ConnectTimeout { get; init; }
}

/// <summary>Factory for the shared AI-provider client and its configurable handler.</summary>
public static class NatterHttpClientFactory
{
    public static SocketsHttpHandler CreateHandler(NatterHttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            UseProxy = options.UseProxy,
        };

        if (options.ConnectTimeout is { } connectTimeout)
            handler.ConnectTimeout = connectTimeout;

        if (!string.IsNullOrWhiteSpace(options.ProxyUrl)
            && Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var proxyUri))
        {
            handler.Proxy = new WebProxy(proxyUri);
        }

        return handler;
    }

    public static HttpClient CreateClient(NatterHttpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HttpClient(CreateHandler(options), disposeHandler: true)
        {
            Timeout = options.Timeout,
        };
    }
}
