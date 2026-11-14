using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Application;

namespace Natter.Application.Dispatching;

/// <summary>
/// Reflection-based request dispatcher. Handlers and pipeline behaviors are resolved
/// from DI, so plugins can contribute either without changing the host.
/// </summary>
public sealed class ApplicationDispatcher : IApplicationDispatcher
{
    private static readonly MethodInfo InvokeMethod = typeof(ApplicationDispatcher)
        .GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IServiceProvider _serviceProvider;

    public ApplicationDispatcher(IServiceProvider serviceProvider)
        => _serviceProvider = serviceProvider;

    public Task<TResponse> SendAsync<TResponse>(
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handlerType = typeof(IRequestHandler<,>).MakeGenericType(requestType, typeof(TResponse));

        if (_serviceProvider.GetService(handlerType) is null)
        {
            throw new InvalidOperationException(
                $"No request handler is registered for '{requestType.FullName}' returning '{typeof(TResponse).Name}'.");
        }

        var invoker = InvokeMethod.MakeGenericMethod(requestType, typeof(TResponse));
        return (Task<TResponse>)invoker.Invoke(null, [_serviceProvider, request, cancellationToken])!;
    }

    private static Task<TResponse> InvokeAsync<TRequest, TResponse>(
        IServiceProvider serviceProvider,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse().ToArray();

        Func<Task<TResponse>> pipeline = () => handler.HandleAsync(request, cancellationToken);
        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.HandleAsync(request, next, cancellationToken);
        }

        return pipeline();
    }
}
