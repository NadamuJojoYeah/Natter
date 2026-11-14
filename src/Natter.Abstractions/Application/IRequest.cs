namespace Natter.Abstractions.Application;

/// <summary>Represents a completed operation without a payload.</summary>
public readonly struct Unit : IEquatable<Unit>
{
    public static Unit Value => default;

    public bool Equals(Unit other) => true;

    public override bool Equals(object? obj) => obj is Unit;

    public override int GetHashCode() => 0;

    public override string ToString() => "()";

    public static bool operator ==(Unit left, Unit right) => true;

    public static bool operator !=(Unit left, Unit right) => false;
}

/// <summary>Marker for an application request returning <typeparamref name="TResponse"/>.</summary>
public interface IRequest<out TResponse>
{
}

/// <summary>Marker for state-changing use cases.</summary>
public interface ICommand<out TResponse> : IRequest<TResponse>
{
}

/// <summary>Marker for read-only use cases.</summary>
public interface IQuery<out TResponse> : IRequest<TResponse>
{
}

/// <summary>Handles exactly one request type. Registered by the application layer or a plugin.</summary>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(TRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-cutting behavior wrapped around every request handler
/// (validation, logging, transactions, ...). Behaviors run in registration order.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> HandleAsync(
        TRequest request,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken = default);
}

/// <summary>In-process request dispatcher (mediator port).</summary>
public interface IApplicationDispatcher
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
