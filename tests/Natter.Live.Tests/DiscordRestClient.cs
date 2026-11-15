using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Natter.Live.Tests;

/// <summary>
/// Minimal Discord REST helper for live tests. It is deliberately independent from the
/// transport under test: it acts as the oracle that observes what the product actually did.
/// The token is only put on the request; it never appears in assertions or output.
/// </summary>
public sealed class DiscordRestClient : IDisposable
{
    private const string ApiBase = "https://discord.com/api/v10";

    private readonly HttpClient _http;

    public DiscordRestClient(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _http = new HttpClient { BaseAddress = new Uri(ApiBase + "/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NatterLiveTests", "1.0"));
    }

    public async Task<JsonElement> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync("users/@me", cancellationToken);

    public async Task<JsonElement> GetGuildsAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync("users/@me/guilds", cancellationToken);

    public async Task<JsonElement> GetGuildChannelsAsync(string guildId, CancellationToken cancellationToken = default)
        => await GetJsonAsync($"guilds/{guildId}/channels", cancellationToken);

    public async Task<JsonElement> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
        => await GetJsonAsync($"channels/{channelId}", cancellationToken);

    public async Task<JsonElement> GetChannelMessagesAsync(
        string channelId,
        int limit = 20,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync($"channels/{channelId}/messages?limit={limit}", cancellationToken);

    public async Task<JsonElement?> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync($"channels/{channelId}/messages/{messageId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public async Task<JsonElement> CreateMessageAsync(
        string channelId,
        string content,
        string? replyToMessageId = null,
        IEnumerable<string>? mentionUserIds = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["content"] = content,
            ["allowed_mentions"] = new
            {
                parse = Array.Empty<string>(),
                users = (mentionUserIds ?? []).ToArray(),
            },
        };

        if (!string.IsNullOrWhiteSpace(replyToMessageId))
        {
            payload["message_reference"] = new
            {
                message_id = replyToMessageId,
                channel_id = channelId,
                fail_if_not_exists = false,
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages")
        {
            Content = JsonContent.Create(payload),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public async Task<JsonElement> EditMessageAsync(
        string channelId,
        string messageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}")
        {
            Content = JsonContent.Create(new { content }),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    public async Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync($"channels/{channelId}/messages/{messageId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Best-effort cleanup; a failure must not mask the assertion that matters.</summary>
    public async Task TryDeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.DeleteAsync($"channels/{channelId}/messages/{messageId}", cancellationToken);
            _ = response.StatusCode;
        }
        catch
        {
            // Cleanup is best effort.
        }
    }

    public async Task TriggerTypingAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync($"channels/{channelId}/typing", content: null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Discord REST {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 400)}",
            null,
            response.StatusCode);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
