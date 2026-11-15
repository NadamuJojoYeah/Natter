using System.Text.Json;

namespace Natter.Live.Tests;

/// <summary>REST-side observations used by the end-to-end tests to check what Discord stored.</summary>
public static class LiveDiscordOracle
{
    public static async Task<JsonElement> CreateInputAsync(DiscordRestClient rest, string channelId, string prefix)
        => await rest.CreateMessageAsync(channelId, $"{prefix} {Guid.NewGuid():N} — please answer this question.");

    /// <summary>Returns bot messages that reply to <paramref name="parentId"/> (newest first).</summary>
    public static async Task<List<JsonElement>> FindRepliesAsync(
        DiscordRestClient rest,
        string channelId,
        string parentId)
    {
        var messages = await rest.GetChannelMessagesAsync(channelId, limit: 50);
        var replies = new List<JsonElement>();

        foreach (var message in messages.EnumerateArray())
        {
            if (!message.TryGetProperty("referenced_message", out var reference)
                || reference.ValueKind != JsonValueKind.Object
                || !reference.TryGetProperty("id", out var referenceId)
                || referenceId.GetString() != parentId)
            {
                continue;
            }

            if (message.TryGetProperty("author", out var author)
                && author.TryGetProperty("bot", out var isBot)
                && isBot.ValueKind == JsonValueKind.True)
            {
                replies.Add(message.Clone());
            }
        }

        return replies;
    }

    public static async Task<JsonElement> WaitForSingleReplyAsync(
        DiscordRestClient rest,
        string channelId,
        string parentId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var replies = new List<JsonElement>();

        while (DateTimeOffset.UtcNow < deadline)
        {
            replies = await FindRepliesAsync(rest, channelId, parentId);
            if (replies.Count > 0)
                break;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return replies.Count > 0
            ? replies[0]
            : throw new TimeoutException($"No bot reply referenced message {parentId} within {timeout}.");
    }
}
