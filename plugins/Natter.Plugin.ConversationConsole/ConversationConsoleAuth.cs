using System.Security.Cryptography;
using System.Text;

namespace Natter.Plugin.ConversationConsole;

/// <summary>
/// Stateless bearer-token authentication for the console (design §4.1). There is no login form and
/// no session cookie: every request either carries the console token or gets a 401. That keeps the
/// plugin's state surface close to zero and removes CSRF from the picture entirely (read-only GETs).
/// </summary>
public sealed class ConversationConsoleTokenAuthenticator
{
    private readonly byte[] _expectedHash;

    public ConversationConsoleTokenAuthenticator(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < ConversationConsoleDefaults.MinTokenLength)
        {
            Problem = token is null or ""
                ? "conversation-console.token-missing"
                : "conversation-console.token-too-short";
            _expectedHash = [];
            return;
        }

        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>True when the configured secret is usable; otherwise <see cref="Problem"/> is set.</summary>
    public bool IsUsable => _expectedHash.Length != 0;

    /// <summary>Stable error code explaining why the secret was rejected.</summary>
    public string? Problem { get; }

    /// <summary>
    /// Validates an <c>Authorization: Bearer …</c> header value. Comparison is constant-time over
    /// SHA-256 digests so the console does not leak the token through timing.
    /// </summary>
    public bool IsAuthorized(string? authorizationHeader)
    {
        if (!IsUsable || string.IsNullOrWhiteSpace(authorizationHeader))
            return false;

        if (!authorizationHeader.StartsWith(ConversationConsoleDefaults.BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var presented = authorizationHeader[ConversationConsoleDefaults.BearerPrefix.Length..].Trim();
        if (presented.Length == 0)
            return false;

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(presentedHash, _expectedHash);
    }

    /// <summary>Actor label used in audit records. Never includes the token or a prefix of it.</summary>
    public static string Actor => "token:console";
}
