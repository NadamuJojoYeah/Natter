using System.Runtime.InteropServices;
using Natter.Cli;
using Natter.Cli.Core;

// Background/job-control shells and some containers start children with SIGINT set to
// SIG_IGN. .NET then does not install a usable handler, so reset the disposition first.
ResetInheritedSignalDisposition();

var console = new SystemConsole();
using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    Cancel();
};

// Console.CancelKeyPress only fires for an attached console. Containers, redirected
// stdin/stdout, nohup and CI still deliver POSIX signals; register both explicitly.
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

return await NatterCli.RunAsync(args, console, cancellationToken: cancellation.Token);

void OnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    Cancel();
}

static void ResetInheritedSignalDisposition()
{
    if (OperatingSystem.IsWindows())
        return;

    try
    {
        signal(2, IntPtr.Zero);   // SIGINT -> SIG_DFL
        signal(15, IntPtr.Zero);  // SIGTERM -> SIG_DFL
    }
    catch (Exception)
    {
        // The managed PosixSignalRegistration below remains the fallback.
    }
}

[DllImport("libc", EntryPoint = "signal")]
static extern IntPtr signal(int signum, IntPtr handler);

void Cancel()
{
    try
    {
        cancellation.Cancel();
    }
    catch (ObjectDisposedException)
    {
    }
}
