using Xunit;

namespace Natter.Live.Tests;

/// <summary>
/// A fact that only runs when the operator opted into live API usage. Without
/// <c>NATTER_LIVE=1</c> the test is reported as skipped, so an offline build stays green.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = settings.Enabled
            ? null
            : $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1 and provide credentials. {settings.SkipReason}";
    }
}

/// <summary>A live fact that also needs a reachable DeepSeek key.</summary>
public sealed class DeepSeekLiveFactAttribute : FactAttribute
{
    public DeepSeekLiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !settings.HasDeepSeek
                ? "DEEPSEEK_API_KEY is not configured."
                : null;
    }
}

/// <summary>A live fact that also needs a bot token, channel id and bot user id.</summary>
public sealed class DiscordLiveFactAttribute : FactAttribute
{
    public DiscordLiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !settings.HasDiscord
                ? "DISCORD_BOT_TOKEN / DISCORD_TEST_CHANNEL_ID / DISCORD_BOT_USER_ID are not configured."
                : null;
    }
}

/// <summary>A live fact that needs a reachable iFlytek Spark MaaS key.</summary>
public sealed class SparkLiveFactAttribute : FactAttribute
{
    public SparkLiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !settings.HasSpark
                ? "SPARK_API_KEY is not configured."
                : null;
    }
}

/// <summary>A live fact that needs DingTalk Stream credentials.</summary>
public sealed class DingTalkLiveFactAttribute : FactAttribute
{
    public DingTalkLiveFactAttribute()
    {
        var settings = LiveSettings.Current;
        Skip = !settings.Enabled
            ? $"Live tests are opt-in: set {LiveSettings.EnableVariable}=1. {settings.SkipReason}"
            : !settings.HasDingTalk
                ? "DINGTALK_CLIENT_ID / DINGTALK_CLIENT_SECRET are not configured."
                : null;
    }
}
