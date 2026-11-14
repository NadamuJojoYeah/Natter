using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Application;

namespace Natter.Infrastructure.Dispatching;

/// <summary>
/// Open-generic pipeline behavior registered by the core module. It demonstrates how
/// cross-cutting concerns wrap every use case without touching handlers.
/// </summary>
public sealed class LoggingPipelineBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingPipelineBehavior<TRequest, TResponse>> _logger;

    public LoggingPipelineBehavior(ILogger<LoggingPipelineBehavior<TRequest, TResponse>> logger)
        => _logger = logger;

    public async Task<TResponse> HandleAsync(
        TRequest request,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken = default)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("→ {RequestName}", requestName);

        try
        {
            var response = await next().ConfigureAwait(false);
            _logger.LogDebug("← {RequestName} in {Elapsed} ms", requestName, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "✖ {RequestName} after {Elapsed} ms", requestName, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
