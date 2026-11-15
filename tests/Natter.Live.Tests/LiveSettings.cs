using System.Text;

namespace Natter.Live.Tests;

/// <summary>
/// Credentials and target ids for live tests. Values come from the process environment first
/// and from <c>artifacts/live/.env</c> (git-ignored) second, so no secret is ever stored in the
/// repository or printed by a test.
/// </summary>
public sealed class LiveSettings
{
    public const string EnableVariable = "NATTER_LIVE";

    private LiveSettings(
        bool enabled,
        string apiKey,
        string botToken,
        string guildId,
        string channelId,
        string botUserId,
        string sparkApiKey,
        string dingTalkClientId,
        string dingTalkClientSecret,
        string dingTalkBotId,
        string? skipReason)
    {
        Enabled = enabled;
        ApiKey = apiKey;
        BotToken = botToken;
        GuildId = guildId;
        ChannelId = channelId;
        BotUserId = botUserId;
        SparkApiKey = sparkApiKey;
        DingTalkClientId = dingTalkClientId;
        DingTalkClientSecret = dingTalkClientSecret;
        DingTalkBotId = dingTalkBotId;
        SkipReason = skipReason;
    }

    public static LiveSettings Current { get; } = Load();

    public bool Enabled { get; }

    public string ApiKey { get; }

    public string BotToken { get; }

    public string GuildId { get; }

    public string ChannelId { get; }

    public string BotUserId { get; }

    public string? SkipReason { get; }

    public bool HasDiscord => !string.IsNullOrWhiteSpace(BotToken)
                              && !string.IsNullOrWhiteSpace(BotUserId)
                              && !string.IsNullOrWhiteSpace(ChannelId);

    public bool HasDeepSeek => !string.IsNullOrWhiteSpace(ApiKey);

    public string SparkApiKey { get; }

    public string DingTalkClientId { get; }

    public string DingTalkClientSecret { get; }

    public string DingTalkBotId { get; }

    public bool HasSpark => !string.IsNullOrWhiteSpace(SparkApiKey);

    public bool HasDingTalk => !string.IsNullOrWhiteSpace(DingTalkClientId)
                               && !string.IsNullOrWhiteSpace(DingTalkClientSecret);

    public static string RepoRoot => FindRepoRoot();

    public static string ArtifactsRoot => Path.Combine(RepoRoot, "artifacts", "live");

    public static string PluginRoot => Path.Combine(ArtifactsRoot, "plugins");

    private static LiveSettings Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reason = new StringBuilder();

        var enabled = string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);
        if (!enabled)
        {
            reason.Append($"{EnableVariable}=1 not set; live tests are opt-in. ");
            ReadDotEnv(values);
            return new LiveSettings(
                false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, reason.ToString());
        }

        foreach (var name in new[]
                 {
                     "DEEPSEEK_API_KEY",
                     "DISCORD_BOT_TOKEN",
                     "DISCORD_TEST_GUILD_ID",
                     "DISCORD_TEST_CHANNEL_ID",
                     "DISCORD_BOT_USER_ID",
                     "SPARK_API_KEY",
                     "DINGTALK_CLIENT_ID",
                     "DINGTALK_CLIENT_SECRET",
                     "DINGTALK_BOT_ID",
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                values[name] = value;
        }

        if (!values.ContainsKey("DEEPSEEK_API_KEY")
            || !values.ContainsKey("DISCORD_BOT_TOKEN")
            || !values.ContainsKey("SPARK_API_KEY"))
            ReadDotEnv(values);

        static string Get(Dictionary<string, string> source, string name)
            => source.TryGetValue(name, out var value) ? value.Trim() : string.Empty;

        return new LiveSettings(
            true,
            Get(values, "DEEPSEEK_API_KEY"),
            Get(values, "DISCORD_BOT_TOKEN"),
            Get(values, "DISCORD_TEST_GUILD_ID"),
            Get(values, "DISCORD_TEST_CHANNEL_ID"),
            Get(values, "DISCORD_BOT_USER_ID"),
            Get(values, "SPARK_API_KEY"),
            Get(values, "DINGTALK_CLIENT_ID"),
            Get(values, "DINGTALK_CLIENT_SECRET"),
            Get(values, "DINGTALK_BOT_ID"),
            reason.Length == 0 ? null : reason.ToString());
    }

    /// <summary>
    /// Credential file lookup order: NATTER_LIVE_ENV, then the user's private config
    /// (~/.config/natter/live.env), then the legacy in-repo artifacts/live/.env.
    /// </summary>
    private static IEnumerable<string> DotEnvCandidates()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NATTER_LIVE_ENV");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".config", "natter", "live.env");

        yield return Path.Combine(FindRepoRoot(), "artifacts", "live", ".env");
    }

    private static void ReadDotEnv(Dictionary<string, string> values)
    {
        foreach (var path in DotEnvCandidates())
        {
            if (!File.Exists(path))
                continue;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var name = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"').Trim('\'');
                if (name.Length > 0 && !values.ContainsKey(name))
                    values[name] = value;
            }

            return;
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
                return directory.FullName;
        }

        return Directory.GetCurrentDirectory();
    }
}
