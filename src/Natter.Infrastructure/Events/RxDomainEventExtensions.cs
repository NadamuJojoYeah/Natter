using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Natter.Abstractions.Events;

namespace Natter.Infrastructure.Events;

/// <summary>Rx helpers tailored to <see cref="DomainEventEnvelope"/> streams.</summary>
public static class RxDomainEventExtensions
{
    /// <summary>Filters the envelope stream down to a concrete domain event type.</summary>
    public static IObservable<TEvent> OfTypeEvent<TEvent>(this IObservable<DomainEventEnvelope> source)
        where TEvent : IDomainEvent
        => source.Where(envelope => envelope.Event is TEvent)
                 .Select(envelope => (TEvent)envelope.Event);

    /// <summary>Projection helper that returns the concrete event and its transport metadata.</summary>
    public static IObservable<(TEvent Event, DomainEventEnvelope Envelope)> OfTypeEventWithEnvelope<TEvent>(
        this IObservable<DomainEventEnvelope> source)
        where TEvent : IDomainEvent
        => source.Where(envelope => envelope.Event is TEvent)
                 .Select(envelope => ((TEvent)envelope.Event, envelope));

    /// <summary>
    /// Subscribes without letting a throwing handler escape into the Rx pipeline.
    /// Prefer this over a raw <c>Subscribe</c> in plugin code.
    /// </summary>
    public static IDisposable SubscribeSafe<T>(
        this IObservable<T> source,
        Action<T> onNext,
        ILogger? logger = null,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);

        return source.Subscribe(new DelegateObserver<T>(onNext, onError, logger));
    }

    private sealed class DelegateObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;
        private readonly Action<Exception>? _onError;
        private readonly ILogger? _logger;

        public DelegateObserver(Action<T> onNext, Action<Exception>? onError, ILogger? logger)
        {
            _onNext = onNext;
            _onError = onError;
            _logger = logger;
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            if (_onError is not null)
            {
                try
                {
                    _onError(error);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Domain event error handler threw.");
                }
            }
            else
            {
                _logger?.LogError(error, "Domain event stream reported an error.");
            }
        }

        public void OnNext(T value)
        {
            try
            {
                _onNext(value);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Domain event subscriber threw.");
            }
        }
    }
}
