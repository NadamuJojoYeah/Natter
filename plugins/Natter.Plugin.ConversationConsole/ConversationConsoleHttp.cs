using System.Text;
using System.Text.Json;

namespace Natter.Plugin.ConversationConsole;

/// <summary>Logical HTTP request, decoupled from <c>HttpListener</c> so handlers stay unit-testable.</summary>
public sealed record ConversationConsoleHttpRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string RemoteAddress);

public sealed record ConversationConsoleHttpResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public static ConversationConsoleHttpResponse Json(
        int statusCode,
        object value,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(
            statusCode,
            "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(value, ConversationConsoleJson.Options),
            headers ?? new Dictionary<string, string>());

    public static ConversationConsoleHttpResponse Error(int statusCode, string code, string message)
        => Json(statusCode, new { error = new { code, message } });

    public static ConversationConsoleHttpResponse Text(int statusCode, string contentType, string value)
        => new(statusCode, contentType, Encoding.UTF8.GetBytes(value), new Dictionary<string, string>());
}

public static class ConversationConsoleJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>Raised on startup failures that must mark the plugin Failed with a stable code.</summary>
public sealed class ConversationConsoleStartupException : Exception
{
    public ConversationConsoleStartupException(string code, string message)
        : base($"{code}: {message}")
        => Code = code;

    public string Code { get; }
}

/// <summary>Stable error codes returned by the API, mirrored by the UI and the tests.</summary>
public static class ConversationConsoleErrorCodes
{
    public const string AuthRequired = "auth.required";
    public const string AuthInvalid = "auth.invalid";
    public const string OriginDenied = "request.origin_denied";
    public const string RouteNotFound = "route.not_found";
    public const string MethodNotAllowed = "method.not_allowed";
    public const string FilterInvalid = "filter.invalid";
    public const string QueryUnavailable = "host.conversation-query.unavailable";
    public const string ServerBusy = "server.busy";
    public const string RequestTimeout = "request.timeout";
    public const string Internal = "internal.error";
}
