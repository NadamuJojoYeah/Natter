using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Natter.Chat.Discord;

/// <summary>
/// Discord gateway close codes (4000-4014) and the recovery path Discord.Net hides behind
/// <c>GatewayReconnectException</c>. The library collapses every reconnectable close into one
/// exception whose message is the fixed string "Server requested a reconnect", so the numeric
/// code has to be recovered from its log lines and from the close-code property on its socket
/// exceptions before <c>lastDisconnectCode</c> can say anything useful.
/// </summary>
public static class DiscordGatewayCloseCode
{
    /// <summary>
    /// Stable disconnect code reported when no numeric gateway close code could be recovered.
    /// Kept for compatibility with the pre-incident <c>lastDisconnectCode</c> value.
    /// </summary>
    public const string DisconnectedCode = "gateway_disconnected";

    private static readonly string[] ContextWords =
    [
        "close", "closed", "gateway", "websocket", "disconnect", "disconnected", "reconnect", "opcode",
    ];

    /// <summary>Properties Discord.Net socket exceptions use to expose the raw close code.</summary>
    private static readonly string[] TypedCodeProperties = ["CloseCode", "GatewayCloseCode", "DismissedCloseCode"];

    // "The server sent close 4009 (Session timed out)" is the Discord.Net wording; the other
    // variants show up in HTTP/websocket exceptions that wrap the same close.
    private static readonly Regex LabelledCodePattern = new(
        @"(?<label>close(?:\s*code)?|code)\s*[:=#]?\s*(?<code>4\d{3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BareCodePattern = new(
        @"(?<!\d)(?<code>4\d{3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="code"/> sits in Discord's gateway close-code range.</summary>
    public static bool IsGatewayCloseCode(int code) => code is >= 4000 and <= 4999;

    /// <summary>
    /// Extracts the first gateway close code mentioned in log or exception text. Codes are only
    /// accepted next to a close/code label or when the text talks about a gateway/websocket
    /// disconnect, so unrelated numbers (latency, message ids) never become a disconnect code.
    /// </summary>
    public static int? TryExtract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var labelled = LabelledCodePattern.Match(text);
        if (labelled.Success && TryParseCode(labelled.Groups["code"].Value, out var labelledCode))
            return labelledCode;

        if (!ContainsContext(text))
            return null;

        var bare = BareCodePattern.Match(text);
        return bare.Success && TryParseCode(bare.Groups["code"].Value, out var bareCode) ? bareCode : null;
    }

    /// <summary>
    /// Extracts a close code from an exception chain. Typed close-code properties win over text
    /// parsing because Discord.Net exposes the raw websocket close there even when the message is
    /// generic.
    /// </summary>
    public static int? TryExtract(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (TryReadTypedCloseCode(current) is { } typed)
                return typed;

            if (TryExtract(current.Message) is { } fromMessage)
                return fromMessage;
        }

        return null;
    }

    /// <summary>Human-readable reason for a known gateway close code.</summary>
    public static string Describe(int code)
        => code switch
        {
            4000 => "unknown error",
            4001 => "unknown opcode",
            4002 => "decode error",
            4003 => "not authenticated",
            4004 => "authentication failed",
            4005 => "already authenticated",
            4006 => "invalid session",
            4007 => "invalid sequence",
            4008 => "rate limited",
            4009 => "session timed out",
            4010 => "invalid shard",
            4011 => "sharding required",
            4012 => "invalid API version",
            4013 => "invalid intent(s)",
            4014 => "disallowed intent(s)",
            _ => "unknown gateway close code",
        };

    /// <summary>Stable disconnect code persisted in health, e.g. <c>gateway_4009</c>.</summary>
    public static string ToDisconnectCode(int? closeCode)
        => closeCode is null ? DisconnectedCode : $"gateway_{closeCode.Value}";

    /// <summary>
    /// Readable disconnect reason. Always states whether the close code was recovered, so health
    /// never again shows a generic message that hides an unknown code (the 2026-11-22 incident).
    /// </summary>
    public static string ToDisconnectMessage(int? closeCode, string? reason)
    {
        var baseReason = string.IsNullOrWhiteSpace(reason)
            ? "gateway disconnected without exception detail"
            : reason.Trim();

        return closeCode is null
            ? $"{baseReason}; closeCode=unknown"
            : $"{baseReason}; closeCode={closeCode.Value} ({Describe(closeCode.Value)})";
    }

    private static bool ContainsContext(string text)
        => ContextWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseCode(string value, out int code)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out code)
           && IsGatewayCloseCode(code);

    private static int? TryReadTypedCloseCode(Exception exception)
    {
        var type = exception.GetType();
        foreach (var name in TypedCodeProperties)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.GetIndexParameters().Length > 0)
                continue;

            object? value;
            try
            {
                value = property.GetValue(exception);
            }
            catch (Exception)
            {
                continue;
            }

            var number = value switch
            {
                int intValue => intValue,
                Enum enumValue => Convert.ToInt32(enumValue, CultureInfo.InvariantCulture),
                _ => -1,
            };

            if (IsGatewayCloseCode(number))
                return number;
        }

        return null;
    }
}
