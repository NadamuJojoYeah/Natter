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
        string? skipReason)
    {
        Enabled = enabled;
        ApiKey = apiKey;
        BotToken = botToken;
        GuildId = guildId;
        ChannelId = channelId;
        BotUserId = botUserId;
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
            return new LiveSettings(false, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, reason.ToString());
        }

        foreach (var name in new[]
                 {
                     "DEEPSEEK_API_KEY",
                     "DISCORD_BOT_TOKEN",
                     "DISCORD_TEST_GUILD_ID",
                     "DISCORD_TEST_CHANNEL_ID",
                     "DISCORD_BOT_USER_ID",
                 })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                values[name] = value;
        }

        if (!values.ContainsKey("DEEPSEEK_API_KEY") || !values.ContainsKey("DISCORD_BOT_TOKEN"))
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
            reason.Length == 0 ? null : reason.ToString());
    }

    private static void ReadDotEnv(Dictionary<string, string> values)
    {
        var path = Path.Combine(FindRepoRoot(), "artifacts", "live", ".env");
        if (!File.Exists(path))
            return;

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
