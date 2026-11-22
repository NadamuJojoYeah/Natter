using System.Security.Cryptography;
using System.Text;

namespace Natter.Plugin.WebConsole;

public enum WebConsoleRole
{
    Viewer,
    Admin,
}

/// <summary>In-memory login session. Sessions die with the plugin process (see known limitations).</summary>
public sealed record WebConsoleSession(
    string Id,
    string Csrf,
    WebConsoleRole Role,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class WebConsoleSessionStore
{
    private readonly Dictionary<string, WebConsoleSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    public WebConsoleSessionStore(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public WebConsoleSession Create(WebConsoleRole role, TimeSpan lifetime)
    {
        var now = _clock.GetUtcNow();
        var session = new WebConsoleSession(
            WebConsoleTokens.CreateToken(),
            WebConsoleTokens.CreateToken(),
            role,
            now,
            now + lifetime);

        lock (_gate)
        {
            PruneLocked(now);
            _sessions[session.Id] = session;
        }

        return session;
    }

    public WebConsoleSession? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            PruneLocked(now);
            return _sessions.TryGetValue(id, out var session) ? session : null;
        }
    }

    public void Revoke(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        lock (_gate)
            _sessions.Remove(id);
    }

    public void Clear()
    {
        lock (_gate)
            _sessions.Clear();
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _sessions.Count;
        }
    }

    private void PruneLocked(DateTimeOffset now)
    {
        if (_sessions.Count == 0)
            return;

        foreach (var id in _sessions.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _sessions.Remove(id);
    }
}

/// <summary>Token verifier using a constant-time comparison over SHA-256 digests.</summary>
public sealed class WebConsoleTokenAuthenticator
{
    private readonly byte[] _expectedDigest;
    private readonly string? _problem;
    private readonly string? _raw;

    public WebConsoleTokenAuthenticator(string? token)
    {
        _raw = token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _problem = "webconsole.token-missing";
            _expectedDigest = [];
            return;
        }

        if (token.Length < WebConsoleDefaults.MinTokenLength)
        {
            _problem = "webconsole.token-too-short";
            _expectedDigest = [];
            return;
        }

        _expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        IsUsable = true;
    }

    public bool IsUsable { get; }

    public string? Problem => _problem;

    /// <summary>
    /// The configured token itself. Only used by the diagnostics bundle to prove it never leaks
    /// (sentinel scan, UI-DIRECTION §3.4); never serialized or logged.
    /// </summary>
    public string? Reveal() => _raw;

    public bool Verify(string? candidate)
    {
        if (!IsUsable || string.IsNullOrEmpty(candidate) || candidate.Length > 512)
            return false;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(digest, _expectedDigest);
    }
}

/// <summary>
/// Resolves the optional read-only credential. Kept separate from the runtime so the resolution rules
/// (missing secret, unusable value, identical to the admin token) are unit-testable without binding a
/// listener.
/// </summary>
public static class WebConsoleCredentials
{
    public const string ViewerSecretMissing = "webconsole.viewer-token-missing";
    public const string ViewerTokenUnusable = "webconsole.viewer-token-unusable";
    public const string ViewerTokenDuplicatesAdmin = "webconsole.viewer-token-duplicates-admin";

    public static WebConsoleTokenAuthenticator? ResolveViewer(
        string? viewerTokenReference,
        Natter.Plugin.Abstractions.IPluginSecrets secrets,
        WebConsoleTokenAuthenticator admin,
        out string? problem)
    {
        problem = null;

        if (string.IsNullOrEmpty(viewerTokenReference))
            return null;

        string? raw;
        try
        {
            raw = secrets.Get(WebConsoleDefaults.ViewerTokenSecretName);
        }
        catch (Natter.Plugin.Abstractions.PluginSecretException)
        {
            problem = ViewerSecretMissing;
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = ViewerSecretMissing;
            return null;
        }

        var viewer = new WebConsoleTokenAuthenticator(raw);
        if (!viewer.IsUsable)
        {
            problem = ViewerTokenUnusable;
            return null;
        }

        if (admin.Verify(raw))
        {
            problem = ViewerTokenDuplicatesAdmin;
            return null;
        }

        return viewer;
    }
}

/// <summary>
/// Failed-login throttle. Two windows:
/// <list type="bullet">
/// <item>per client: <see cref="MaxFailuresPerClient"/> failures in <see cref="Window"/> (5 min);</item>
/// <item>global backstop: <see cref="MaxFailuresGlobal"/> failures in <see cref="GlobalWindow"/> (15 min).</item>
/// </list>
/// The global cap used to be a one-way trap: once it tripped, every client was refused for the full
/// window and <c>Reset</c> only cleared a single client. It is now recoverable — a successful login
/// releases the oldest global entries (S2-D1), <see cref="Reset"/> removes that client from both
/// windows, and <see cref="ResetAll"/> clears the global backstop explicitly (ops recovery).
/// </summary>
public sealed class WebConsoleLoginThrottle
{
    public const int MaxFailuresPerClient = 5;
    public const int MaxFailuresGlobal = 50;

    /// <summary>How many oldest global entries one successful login releases.</summary>
    public const int GlobalRecoveryPerSuccess = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Global backstop window; longer than the per-client window so a burst decays gradually.</summary>
    public static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly List<GlobalFailure> _global = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _clock;

    public WebConsoleLoginThrottle(TimeProvider clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsBlocked(string clientKey, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            PruneLocked(now);

            if (_failures.TryGetValue(clientKey, out var entries) && entries.Count >= MaxFailuresPerClient)
            {
                retryAfter = RetryAfter(entries, now, Window);
                return true;
            }

            if (_global.Count >= MaxFailuresGlobal)
            {
                retryAfter = RetryAfter(_global.Select(entry => entry.At).ToList(), now, GlobalWindow);
                return true;
            }

            return false;
        }
    }

    public void RecordFailure(string clientKey)
    {
        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            PruneLocked(now);

            if (!_failures.TryGetValue(clientKey, out var entries))
            {
                entries = [];
                _failures[clientKey] = entries;
            }

            entries.Add(now);
            _global.Add(new GlobalFailure(clientKey, now));
        }
    }

    /// <summary>
    /// A successful login proves this client is not the attacker: its failures leave both windows,
    /// and up to <see cref="GlobalRecoveryPerSuccess"/> oldest global entries are released so a
    /// saturated backstop cannot lock every operator out (S2-D1).
    /// </summary>
    public void RecordSuccess(string clientKey)
    {
        lock (_gate)
        {
            // The client's own failures leave both windows…
            _failures.Remove(clientKey);
            _global.RemoveAll(entry => string.Equals(entry.Client, clientKey, StringComparison.Ordinal));

            // …and a saturated backstop releases a bounded slice so operators can get back in.
            if (_global.Count >= MaxFailuresGlobal)
            {
                var release = Math.Min(GlobalRecoveryPerSuccess, _global.Count);
                _global.RemoveRange(0, release);
            }
        }
    }

    /// <summary>Clears one client from both windows (a client-scoped reset, e.g. after ops verified it).</summary>
    public void Reset(string clientKey)
    {
        lock (_gate)
        {
            _failures.Remove(clientKey);
            _global.RemoveAll(entry => string.Equals(entry.Client, clientKey, StringComparison.Ordinal));
        }
    }

    /// <summary>Clears every failure and the global backstop (explicit operator recovery).</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _failures.Clear();
            _global.Clear();
        }
    }

    /// <summary>Diagnostics: current global failure count inside the global window.</summary>
    public int GlobalFailureCount
    {
        get
        {
            lock (_gate)
            {
                PruneLocked(_clock.GetUtcNow());
                return _global.Count;
            }
        }
    }

    private static TimeSpan RetryAfter(List<DateTimeOffset> entries, DateTimeOffset now, TimeSpan window)
    {
        if (entries.Count == 0)
            return TimeSpan.Zero;

        var oldest = entries.Min();
        var remaining = window - (now - oldest);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var key in _failures
                     .Where(pair => pair.Value.Count == 0 || now - pair.Value[^1] >= Window)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _failures.Remove(key);
        }

        _global.RemoveAll(entry => now - entry.At >= GlobalWindow);
    }

    private readonly record struct GlobalFailure(string Client, DateTimeOffset At);
}

/// <summary>Origin / Sec-Fetch-Site policy for browser requests (design §6.1).</summary>
public sealed class WebConsoleOriginPolicy
{
    private readonly HashSet<string> _allowedOrigins;

    public WebConsoleOriginPolicy(string? publicBaseUrl, int port)
    {
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"http://127.0.0.1:{port}",
            $"http://localhost:{port}",
            $"http://[::1]:{port}",
        };

        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && WebConsoleOptionsValidator.TryParseBaseUrl(publicBaseUrl!, out var baseUri)
            && baseUri is not null)
        {
            _allowedOrigins.Add(baseUri.GetLeftPart(UriPartial.Authority));
        }
    }

    public IReadOnlyCollection<string> AllowedOrigins => _allowedOrigins;

    public bool RequiresSecureCookie
        => _allowedOrigins.Any(origin => origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public bool IsAllowed(string? origin, string? secFetchSite)
    {
        if (string.Equals(secFetchSite, "cross-site", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        return _allowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority));
    }
}

public static class WebConsoleTokens
{
    public static string CreateToken(int byteCount = 32)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public static class WebConsoleCookies
{
    public static string BuildSessionCookie(string sessionId, int maxAgeSeconds, bool secure)
        => $"{WebConsoleDefaults.SessionCookieName}={sessionId}; Path=/; HttpOnly; SameSite=Strict; Max-Age={maxAgeSeconds}"
           + (secure ? "; Secure" : string.Empty);

    public static string BuildExpiredCookie(bool secure)
        => $"{WebConsoleDefaults.SessionCookieName}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0"
           + (secure ? "; Secure" : string.Empty);

    public static string? ReadCookie(string? cookieHeader, string name)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
            return null;

        var values = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                continue;

            if (string.Equals(value[..separator], name, StringComparison.Ordinal))
                return value[(separator + 1)..];
        }

        return null;
    }
}
