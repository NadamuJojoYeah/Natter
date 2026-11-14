using Microsoft.Extensions.Logging;

namespace Natter.Infrastructure.Events;

/// <summary>Isolates subscriber failures so one broken observer cannot poison the Rx stream.</summary>
internal sealed class SafeObserver<T> : IObserver<T>
{
    private readonly IObserver<T> _inner;
    private readonly ILogger _logger;

    public SafeObserver(IObserver<T> inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public void OnCompleted()
    {
        try
        {
            _inner.OnCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain event observer threw while completing.");
        }
    }

    public void OnError(Exception error)
    {
        try
        {
            _inner.OnError(error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain event observer threw while handling an error.");
        }
    }

    public void OnNext(T value)
    {
        try
        {
            _inner.OnNext(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Domain event observer {Observer} threw while handling an event.",
                _inner.GetType().FullName);
        }
    }
}
