using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Natter.Live.Tests;

/// <summary>
/// Stages the built plugin packages into a file-system plugin root the CLI can load, and runs
/// the CLI as a separate process so deployment concerns (load context, staging, secrets from
/// the environment) are part of the test instead of being hidden by in-process references.
/// </summary>
public static class LiveDeployment
{
    public const string PluginRootVariable = "NATTER_LIVE_PLUGIN_ROOT";

    public static string PluginRoot
        => Environment.GetEnvironmentVariable(PluginRootVariable)
           ?? Path.Combine(LiveSettings.ArtifactsRoot, "plugins");

    private static readonly Lock PrepareGate = new();
    private static bool _prepared;

    /// <summary>Copies the freshly built plugin packages (lib/, manifest, schema, config) into a root.</summary>
    public static void StageInto(string root, ITestOutputHelper? output = null)
    {
        var discordSource = FindBuildOutput("samples/Natter.Sample.Plugin.Discord", "Natter.Sample.Plugin.Discord");
        var deepSeekSource = FindBuildOutput("src/Natter.Feature.Ai.DeepSeek", "Natter.Feature.Ai.DeepSeek");

        Stage(discordSource, Path.Combine(root, "discord"), DiscordConfig(), output);
        Stage(deepSeekSource, Path.Combine(root, "deepseek"), DeepSeekConfig(), output);
    }

    /// <summary>Stages once per test process for the shared live plugin root.</summary>
    public static void Prepare(string root, ITestOutputHelper? output = null)
    {
        lock (PrepareGate)
        {
            if (_prepared)
                return;

            StageInto(root, output);
            _prepared = true;
        }
    }

    public static ProcessStartInfo CliStartInfo(string pluginRoot, params string[] arguments)
    {
        var cliDll = FindCliDll();
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = LiveSettings.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(cliDll);
        info.ArgumentList.Add("--plugin-dir");
        info.ArgumentList.Add(pluginRoot);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        return info;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string pluginRoot,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout,
        params string[] arguments)
    {
        var info = CliStartInfo(pluginRoot, arguments);
        foreach (var (name, value) in environment)
        {
            if (value is null)
                info.Environment.Remove(name);
            else
                info.Environment[name] = value;
        }

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException("Could not start the natter CLI process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"natter {string.Join(' ', arguments)} did not exit within {timeout}.");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static void Stage(string source, string destination, string configJson, ITestOutputHelper? output)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);

        Directory.CreateDirectory(destination);
        CopyDirectory(Path.Combine(source, "lib"), Path.Combine(destination, "lib"));

        foreach (var file in new[] { "plugin.json", "config.schema.json" })
            File.Copy(Path.Combine(source, file), Path.Combine(destination, file), overwrite: true);

        File.WriteAllText(Path.Combine(destination, "config.json"), configJson);
        output?.WriteLine($"staged {destination} from {source}");
    }

    private static string DiscordConfig()
    {
        var settings = LiveSettings.Current;
        return $$"""
            {
              "token": "${secret:DISCORD_BOT_TOKEN}",
              "selfId": "{{settings.BotUserId}}",
              "intents": ["Guilds", "GuildMessages", "DirectMessages", "MessageContent"],
              "guilds": ["{{settings.GuildId}}"],
              "channels": ["{{settings.ChannelId}}"],
              "requireMention": true,
              "ignoreBots": true,
              "ignoreSelf": true,
              "typing": false,
              "messageCommands": true,
              "slashCommands": false,
              "maxMessageLength": 1900,
              "readyTimeoutSeconds": 60
            }
            """;
    }

    private static string DeepSeekConfig()
        => """
           {
             "apiKey": "${secret:DEEPSEEK_API_KEY}",
             "model": "deepseek-flash",
             "thinking": "disabled",
             "reasoningEffort": "none",
             "historyMaxMessages": 8,
             "maxOutputTokens": 256,
             "timeoutSeconds": 60,
             "tools": []
           }
           """;

    private static string FindBuildOutput(string projectDirectory, string assemblyName)
    {
        var bin = Path.Combine(LiveSettings.RepoRoot, projectDirectory, "bin");
        if (!Directory.Exists(bin))
            throw new DirectoryNotFoundException($"Build '{projectDirectory}' first; '{bin}' does not exist.");

        var candidates = Directory.GetDirectories(bin, "net10.0", SearchOption.AllDirectories)
            .Select(directory => new DirectoryInfo(directory))
            .Where(directory => File.Exists(Path.Combine(directory.FullName, "lib", assemblyName + ".deps.json")))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        return candidates.Count > 0
            ? candidates[0].FullName
            : throw new DirectoryNotFoundException(
                $"No staged '{assemblyName}' package under '{bin}'. Run: dotnet build {projectDirectory}.");
    }

    private static string FindCliDll()
    {
        var bin = Path.Combine(LiveSettings.RepoRoot, "src", "Natter.Cli", "bin");
        var candidates = Directory.GetDirectories(bin, "net10.0", SearchOption.AllDirectories)
            .Select(directory => Path.Combine(directory, "natter.dll"))
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .ToList();

        return candidates.Count > 0
            ? candidates[0].FullName
            : throw new FileNotFoundException($"Build the CLI first: no natter.dll under '{bin}'.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public static string WithoutSecrets(string text, params string[] secrets)
    {
        var result = text;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrWhiteSpace(secret))
                result = result.Replace(secret, "***", StringComparison.Ordinal);
        }

        return result;
    }

    public static string Tail(string text, int max = 4000)
        => text.Length <= max ? text : text[^max..];
}
