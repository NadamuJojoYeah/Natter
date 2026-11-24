using System.Text;
using Natter.Application.Redaction;
using Natter.Cli.Abstractions;

namespace Natter.Cli.Core.Logging;

/// <summary>
/// Decorator around the host console: every write goes through the shared redaction core before
/// it reaches stdout/stderr. Installed once in the CLI composition root, so every current and
/// future command inherits it; command authors do not need to remember a Sanitize call.
/// </summary>
public sealed class RedactingConsole : IConsole
{
    private readonly IConsole _inner;

    public RedactingConsole(IConsole inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Out = new RedactingTextWriter(inner.Out);
        Error = new RedactingTextWriter(inner.Error);
    }

    /// <summary>Idempotent: wrapping an already-redacting console returns it unchanged.</summary>
    public static IConsole Wrap(IConsole console)
        => console is RedactingConsole ? console : new RedactingConsole(console);

    public TextWriter Out { get; }

    public TextWriter Error { get; }

    public bool IsInteractive => _inner.IsInteractive;

    public string? ReadLine() => _inner.ReadLine();

    private sealed class RedactingTextWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => inner.Write(Redact(value.ToString()));

        public override void Write(string? value) => inner.Write(Redact(value));

        public override void WriteLine() => inner.WriteLine();

        public override void WriteLine(string? value) => inner.WriteLine(Redact(value));

        public override void Flush() => inner.Flush();

        public override Task WriteAsync(char value) => inner.WriteAsync(Redact(value.ToString()));

        public override Task WriteAsync(string? value) => inner.WriteAsync(Redact(value));

        public override Task WriteLineAsync(string? value) => inner.WriteLineAsync(Redact(value));

        public override Task WriteLineAsync() => inner.WriteLineAsync();

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private static string Redact(string? value)
            => string.IsNullOrEmpty(value)
                ? value ?? string.Empty
                : SecretRedaction.Apply(value, SecretRedactionOptions.Admin);
    }
}
