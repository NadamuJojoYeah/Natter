using System.Net;
using System.Text;
using System.Text.Json;

namespace Natter.Cli.Core.Commands;

/// <summary>Loopback-only optional health endpoint for `natter serve --health-port`.</summary>
public sealed class HealthHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<ValueTask<object>> _snapshot;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

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

            _ = HandleAsync(context);
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

            var body = JsonSerializer.Serialize(await _snapshot().ConfigureAwait(false), JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            context.Response.Close();
        }
        catch
        {
            try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
        }
    }
}
