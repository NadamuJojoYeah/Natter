using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Application;
using Natter.Application.Dispatching;
using Xunit;

namespace Natter.Framework.Tests;

public sealed class ApplicationDispatcherTests
{
    [Fact]
    public async Task Dispatches_to_handler_and_runs_pipeline_in_order()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApplicationDispatcher, ApplicationDispatcher>();
        services.AddTransient<IRequestHandler<PingCommand, string>, PingHandler>();
        services.AddTransient<IPipelineBehavior<PingCommand, string>, FirstBehavior>();
        services.AddTransient<IPipelineBehavior<PingCommand, string>, SecondBehavior>();
        services.AddSingleton<CallLog>();

        await using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IApplicationDispatcher>();

        var response = await dispatcher.SendAsync(new PingCommand("payload"));

        Assert.Equal("first(second(handler:payload))", response);
        Assert.Equal(new[] { "first", "second", "handler", "second-end", "first-end" }, provider.GetRequiredService<CallLog>().Entries);
    }

    [Fact]
    public async Task Throws_when_no_handler_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApplicationDispatcher, ApplicationDispatcher>();
        await using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IApplicationDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.SendAsync(new PingCommand("x")));
    }

    private sealed record PingCommand(string Payload) : ICommand<string>;

    private sealed class PingHandler(CallLog log) : IRequestHandler<PingCommand, string>
    {
        public Task<string> HandleAsync(PingCommand request, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("handler");
            return Task.FromResult($"handler:{request.Payload}");
        }
    }

    private sealed class FirstBehavior(CallLog log) : IPipelineBehavior<PingCommand, string>
    {
        public async Task<string> HandleAsync(PingCommand request, Func<Task<string>> next, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("first");
            var response = await next();
            log.Entries.Add("first-end");
            return $"first({response})";
        }
    }

    private sealed class SecondBehavior(CallLog log) : IPipelineBehavior<PingCommand, string>
    {
        public async Task<string> HandleAsync(PingCommand request, Func<Task<string>> next, CancellationToken cancellationToken = default)
        {
            log.Entries.Add("second");
            var response = await next();
            log.Entries.Add("second-end");
            return $"second({response})";
        }
    }

    private sealed class CallLog
    {
        public List<string> Entries { get; } = [];
    }
}
