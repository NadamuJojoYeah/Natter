using System.Net;
using System.Text;
using Natter.Cli.Core.Rendering;

namespace Natter.Cli.Core.Commands;

/// <summary>Loopback-only optional health endpoint for `natter serve --health-port`.</summary>
public sealed class HealthHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<ValueTask<object>> _snapshot;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// S2-E9: bound the number of concurrent requests. Without a limit a local misbehaving client
    /// could start an unbounded number of handler tasks (and snapshot builds) per second.
    /// </summary>
    private const int MaxConcurrentRequests = 4;

    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public HealthHttpServer(int port, Func<ValueTask<object>> snapshot)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();

        // Shutdown must never throw, but it must not hide a dead accept loop either: a faulted loop
        // means /health stopped answering while `serve` looks healthy. Report it once, then continue.
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException or ObjectDisposedException)
        {
            var reason = exception is AggregateException aggregate && aggregate.InnerException is { } inner
                ? inner
                : exception;

            ReportLoopTerminated(reason);
        }

        _cts.Dispose();
        _requestSlots.Dispose();
    }

    /// <summary>
    /// Writes one diagnostic line to stderr. Deliberately excludes the request URI: a health URL can
    /// carry nothing today, but the rule is that request detail never reaches a log line (S3 D-02).
    /// </summary>
    internal static Action<string> Report { get; set; } =
        message => Console.Error.WriteLine($"health endpoint: {message}");

    private static void ReportLoopTerminated(Exception exception)
        => Report($"accept loop terminated: {exception.GetType().Name}: {exception.Message}");

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = HandleWithSlotAsync(context);
        }
    }

    private async Task HandleWithSlotAsync(HttpListenerContext context)
    {
        // Reject instead of queueing: a health probe that cannot be served promptly should fail fast.
        if (!await _requestSlots.WaitAsync(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false))
        {
            try
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
            }
            catch
            {
                // Response may already be gone; nothing else to do.
            }

            return;
        }

        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _requestSlots.Release();
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath != "/health")
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            var body = JsonOutput.SerializeCompact(await _snapshot().ConfigureAwait(false));
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception exception)
        {
            // A failed 500 write means the client is already gone; nothing to recover, but the cause
            // must not vanish silently (S3 D-02).
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception writeFailure) when (writeFailure is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                Report($"could not answer a request: {writeFailure.GetType().Name}: {writeFailure.Message}");
            }

            if (exception is not OperationCanceledException)
                Report($"request failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
