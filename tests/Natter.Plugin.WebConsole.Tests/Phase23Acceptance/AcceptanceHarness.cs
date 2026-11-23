using System.Net;
using System.Text;
using System.Text.Json;
using Natter.Application.Admin;
using Natter.Plugin.WebConsole;

namespace Natter.Plugin.WebConsole.Tests.Phase23Acceptance;

/// <summary>
/// Independent Phase 2/3 acceptance harness (task-5, qa).
///
/// Why an own harness: this test project only references the plugin assembly, so the host
/// implementation (<c>HostAdminConfigStore</c> / <c>CliAdminCommandRunner</c>, assembly
/// <c>Natter.Infrastructure</c>) is not reachable from here. The real host wiring is verified
/// end-to-end against an isolated <c>natter serve</c> (see
/// docs/testing/2026-11-21-webconsole-phase23-acceptance.md §2); this harness verifies the HTTP
/// contract, the not-implemented (501) gap behaviour and the output/redaction/timeout borders that
/// are hard to provoke through a live console.
///
/// <see cref="FileAdminConfigStore"/> is a contract-faithful reference store over a real temp
/// directory (real ETag, real backup, real write lock, real symlink rejection) so the assertions
/// about "nothing was written" are real filesystem assertions.
/// </summary>
internal sealed class FileAdminConfigStore(string root) : IAdminConfigStore
{
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<(string Id, string ETag, string Content)> _undo = [];

    public string Root { get; } = root;

    public bool AllowPluginDocuments { get; init; } = true;

    /// <summary>Plugin ids the (reference) host knows; unknown plugin documents map to 404.</summary>
    public IReadOnlySet<string> KnownPlugins { get; init; } = new HashSet<string>(["natter.tool.admin.webconsole"], StringComparer.Ordinal);

    public bool FailEverySave { get; init; }

    public string PathFor(string id) => Path.Combine(Root, id == "profiles" ? "profiles.json" : id["plugin:".Length..] + ".json");

    public string BackupRoot => Path.Combine(Root, ".backup");

    public ValueTask<IReadOnlyList<AdminConfigDocumentInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string> { "profiles" };
        if (AllowPluginDocuments)
            ids.Add("plugin:natter.tool.admin.webconsole");

        var documents = ids
            .Select(id => Describe(id))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<AdminConfigDocumentInfo>>(documents);
    }

    public ValueTask<AdminConfigReadResult> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(id, out var normalized, out var code, out var message))
            return ValueTask.FromResult(AdminConfigReadResult.Failed(code, message));

        if (AdminConfigDocumentPolicy.TryGetPluginId(normalized, out var readPluginId)
            && !KnownPlugins.Contains(readPluginId))
        {
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigPluginUnknown,
                $"Plugin '{readPluginId}' is not registered in this host."));
        }

        var path = PathFor(normalized);
        if (IsSymlink(path))
        {
            return ValueTask.FromResult(AdminConfigReadResult.Failed(
                AdminCodes.ConfigSymlinkNotAllowed,
                $"Config document '{normalized}' is a symbolic link and will not be read."));
        }

        var exists = File.Exists(path);
        var content = exists ? File.ReadAllText(path) : string.Empty;
        return ValueTask.FromResult(AdminConfigReadResult.Ok(new AdminConfigDocument(
            normalized,
            content,
            ETagOf(content),
            exists ? File.GetLastWriteTimeUtc(path) : DateTimeOffset.UnixEpoch,
            exists,
            exists ? AdminConfigDocumentSource.ConfigDirectory : AdminConfigDocumentSource.Missing,
            AdminConfigReloadHint.RestartHost)));
    }

    public ValueTask<AdminConfigValidation> ValidateAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(id, out var normalized, out var code, out var message))
        {
            return ValueTask.FromResult(new AdminConfigValidation(
                id ?? string.Empty,
                false,
                code,
                ETagOf(content),
                Encoding.UTF8.GetByteCount(content),
                false,
                [new AdminConfigIssue("/id", code, message)],
                AdminConfigReloadHint.None));
        }

        var issues = ValidateContent(content);
        return ValueTask.FromResult(new AdminConfigValidation(
            normalized,
            issues.Count == 0,
            issues.Count == 0 ? AdminCodes.ConfigValid : AdminCodes.ConfigInvalid,
            ETagOf(content),
            Encoding.UTF8.GetByteCount(content),
            content.Contains("sk-", StringComparison.Ordinal),
            issues,
            AdminConfigReloadHint.RestartHost));
    }

    public async ValueTask<AdminConfigSaveResult> SaveAsync(
        string id,
        string content,
        string? etag,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(id, out var normalized, out var code, out var message))
            return Reject(normalized: id ?? string.Empty, code, message);

        var issues = ValidateContent(content);
        if (issues.Count > 0)
            return Reject(normalized, AdminCodes.ConfigInvalid, "Config content failed validation.", issues);

        if (AdminConfigDocumentPolicy.TryGetPluginId(normalized, out var savePluginId)
            && !KnownPlugins.Contains(savePluginId))
        {
            return Reject(normalized, AdminCodes.ConfigPluginUnknown, $"Plugin '{savePluginId}' is not registered in this host.");
        }

        if (FailEverySave)
            return Reject(normalized, AdminCodes.ConfigWriteFailed, "The document could not be written; the previous content is unchanged.");

        // The reference store always serializes writes: the browser-visible race is the ETag one.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Root);
            var path = PathFor(normalized);
            if (IsSymlink(path))
            {
                return Reject(
                    normalized,
                    AdminCodes.ConfigSymlinkNotAllowed,
                    $"Config document '{normalized}' is a symbolic link and will not be overwritten.");
            }

            var exists = File.Exists(path);
            var current = exists ? File.ReadAllText(path) : string.Empty;
            var currentETag = ETagOf(current);
            var conflict = etag is null ? exists : !string.Equals(etag, currentETag, StringComparison.Ordinal);
            if (conflict)
            {
                return Reject(
                    normalized,
                    AdminCodes.ConfigEtagMismatch,
                    exists
                        ? "The document changed since it was read; reload before saving."
                        : "The document does not exist yet; pass a null ETag to create it.",
                    conflictETag: currentETag);
            }

            string? backupPath = null;
            if (exists)
            {
                var directory = Path.Combine(BackupRoot, DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'"));
                Directory.CreateDirectory(directory);
                var backup = Path.Combine(directory, Path.GetFileName(path));
                File.WriteAllText(backup, current);
                File.SetUnixFileMode(backup, FileMode600);
                backupPath = Path.GetRelativePath(Root, backup);
                _undo.Add((normalized, currentETag, current));
            }

            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content);
            File.SetUnixFileMode(temp, FileMode600);
            File.Move(temp, path, overwrite: true);

            return new AdminConfigSaveResult(
                true,
                AdminCodes.ConfigSaved,
                "Config saved.",
                normalized,
                ETagOf(content),
                null,
                backupPath,
                [],
                null,
                AdminConfigReloadHint.RestartHost);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<(string Id, string ETag, string Content)> UndoSnapshot
    {
        get
        {
            lock (_undo)
                return _undo.ToArray();
        }
    }

    /// <summary>
    /// S2-F1: the ETag algorithm is NOT re-implemented here. It delegates to the product
    /// <see cref="AdminETag"/> so a contract change (prefix, casing, encoding) cannot leave the
    /// acceptance harness green while the host behaves differently. See also
    /// <c>HarnessEtags_match_the_product_implementation</c> in <c>ConfigPhase2AcceptanceTests</c>.
    /// </summary>
    public static string ETagOf(string content) => AdminETag.Compute(content);

    private AdminConfigDocumentInfo Describe(string id)
    {
        var path = PathFor(id);
        var exists = File.Exists(path) && !IsSymlink(path);
        var content = exists ? File.ReadAllText(path) : string.Empty;
        return new AdminConfigDocumentInfo(
            id,
            exists,
            ETagOf(content),
            exists ? File.GetLastWriteTimeUtc(path) : null,
            exists ? AdminConfigDocumentSource.ConfigDirectory : AdminConfigDocumentSource.Missing,
            true,
            AdminConfigReloadHint.RestartHost);
    }

    private AdminConfigSaveResult Reject(
        string normalized,
        string code,
        string message,
        IReadOnlyList<AdminConfigIssue>? issues = null,
        string? conflictETag = null)
        => new(
            false,
            code,
            message,
            normalized,
            null,
            conflictETag,
            null,
            issues ?? [],
            null,
            AdminConfigReloadHint.RestartHost);

    private static IReadOnlyList<AdminConfigIssue> ValidateContent(string content)
    {
        var issues = new List<AdminConfigIssue>();
        if (Encoding.UTF8.GetByteCount(content) > WebConsoleConfigEditPolicy.MaxContentBytes)
            issues.Add(new AdminConfigIssue("/content", AdminCodes.ConfigTooLarge, "Config content is too large."));
        if (content.Contains('\0', StringComparison.Ordinal))
            issues.Add(new AdminConfigIssue("/content", AdminCodes.ConfigControlChars, "Config content contains NUL characters."));

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                issues.Add(new AdminConfigIssue("$", AdminCodes.ConfigRootType, "Config root must be an object or array."));
        }
        catch (JsonException exception)
        {
            issues.Add(new AdminConfigIssue("$", AdminCodes.ConfigInvalidJson, exception.Message));
        }

        return issues;
    }

    private static bool TryNormalize(string? id, out string normalized, out string code, out string message)
        => AdminConfigDocumentPolicy.TryNormalize(id, out normalized, out code, out message);

    private static bool IsSymlink(string path) => new FileInfo(path).LinkTarget is not null;
}

/// <summary>Scripted <see cref="IAdminCommandRunner"/> that never starts a process.</summary>
internal sealed class ScriptedCommandRunner : IAdminCommandRunner
{
    public bool Available { get; init; } = true;

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public string Stdout { get; init; } = "ok";

    public string Stderr { get; init; } = string.Empty;

    public int ExitCode { get; init; }

    public string? CodeOverride { get; init; }

    public bool HonorCancellation { get; init; } = true;

    public List<(string Command, IReadOnlyList<string> Args)> Calls { get; } = [];

    public async ValueTask<AdminCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((command, args));
        var started = DateTimeOffset.UtcNow;
        var timedOut = false;

        if (Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(Delay, HonorCancellation ? cancellationToken : CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (HonorCancellation)
            {
                timedOut = true;
            }
        }

        var bytes = Encoding.UTF8.GetByteCount(Stdout) + Encoding.UTF8.GetByteCount(Stderr);
        return new AdminCommandResult(
            Executed: true,
            Code: CodeOverride ?? (timedOut ? AdminCodes.CommandTimeout : (ExitCode == 0 ? AdminCodes.CommandSucceeded : AdminCodes.CommandFailed)),
            Message: timedOut ? "Command exceeded its budget." : "Command finished.",
            Command: command,
            Args: args,
            ExitCode: ExitCode,
            Stdout: Stdout,
            Stderr: Stderr,
            Truncated: false,
            OutputBytes: bytes,
            DurationMs: (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            TimedOut: timedOut);
    }
}

/// <summary>Minimal redactor for the injected surface (plugin policy redacts command output itself).</summary>
internal sealed class AcceptanceRedactor : IAdminRedactor
{
    public string Redact(string? value) => value is null ? string.Empty : WebConsoleRedaction.RedactSecrets(value);
}

internal sealed class AcceptanceLogReader : IAdminLogReader
{
    public ValueTask<IReadOnlyList<AdminLogEntry>> ReadAsync(int limit, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<AdminLogEntry>>([]);
}

internal sealed class AcceptanceHarness : IDisposable
{
    public const string Token = "acceptance-token-0123456789abcdef";

    private AcceptanceHarness(
        WebConsoleOptions options,
        WebConsoleRequestHandler handler,
        FileAdminConfigStore? store,
        ScriptedCommandRunner? runner,
        string directory,
        FakeTimeProvider clock)
    {
        Options = options;
        Handler = handler;
        Store = store;
        Runner = runner;
        Directory = directory;
        Clock = clock;
    }

    public WebConsoleOptions Options { get; }

    public WebConsoleRequestHandler Handler { get; }

    public FileAdminConfigStore? Store { get; }

    public ScriptedCommandRunner? Runner { get; }

    public FakeTimeProvider Clock { get; }

    public string Directory { get; }

    public string AuditDirectory => Path.Combine(Directory, "audit");

    public string AuditFile => Path.Combine(AuditDirectory, WebConsoleDefaults.AuditFileName);

    public string ConfigRoot => Store?.Root ?? Path.Combine(Directory, "config");

    public static AcceptanceHarness Create(
        bool withStore = true,
        bool withRunner = true,
        WebConsoleOptions? options = null,
        ScriptedCommandRunner? runner = null,
        bool allowConfigWrite = true,
        bool allowCommandConsole = true,
        int commandTimeoutSeconds = 5)
    {
        options ??= new WebConsoleOptions
        {
            Port = 5191,
            ReadOnly = false,
            AllowConfigWrite = allowConfigWrite,
            AllowCommandConsole = allowCommandConsole,
            CommandAllowlist = ["health", "plugins list"],
            CommandTimeoutSeconds = commandTimeoutSeconds,
            MaxLogLines = 100,
        };

        var directory = Path.Combine(
            Path.GetTempPath(),
            "natter-webconsole-acceptance",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var wwwroot = Path.Combine(directory, "wwwroot");
        System.IO.Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<!doctype html><html><body></body></html>");
        File.WriteAllText(Path.Combine(wwwroot, "app.js"), "\"use strict\";");
        File.WriteAllText(Path.Combine(wwwroot, "app.css"), "body { color: #fff; }");

        var store = withStore ? new FileAdminConfigStore(Path.Combine(directory, "config")) : null;
        var commandRunner = withRunner ? runner ?? new ScriptedCommandRunner() : null;

        var identity = new WebConsoleIdentity(
            "natter.tool.admin.webconsole",
            "Natter WebConsole",
            "0.1.0",
            "tool",
            "0.1.0",
            "Natter.Plugin.WebConsole",
            directory,
            Path.Combine(directory, "plugin.json"));

        var config = JsonSerializer.SerializeToElement(new
        {
            port = options.Port,
            token = WebConsoleDefaults.TokenReference,
            readOnly = options.ReadOnly,
            allowConfigWrite = options.AllowConfigWrite,
            allowCommandConsole = options.AllowCommandConsole,
            nested = new { password = "hunter2", apiKey = "sk-abcdefghijklmnop" },
        });

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
        var preview = WebConsoleConfig.Build(config, "acceptance", null, options);
        var tokens = new WebConsoleTokenAuthenticator(Token);
        var assets = WebConsoleStaticAssets.Load(wwwroot);
        var audit = new WebConsoleAuditStore(Path.Combine(directory, "audit"), clock);
        var surface = new WebConsoleAdminSurface(store, commandRunner, new AcceptanceLogReader(), new AcceptanceRedactor(), null);
        var admin = new WebConsoleAdminGateway(surface, options, clock);
        var status = new WebConsoleServerStatusHolder();
        status.Set(WebConsoleServerStatus.Listening($"http://127.0.0.1:{options.Port}"));

        var diagnostics = new WebConsoleDiagnostics(
            identity,
            options,
            preview,
            UnavailableWebConsoleHostPort.Instance,
            admin,
            status,
            audit,
            tokens,
            assets,
            clock);

        var handler = new WebConsoleRequestHandler(
            options,
            tokens,
            new WebConsoleSessionStore(clock),
            new WebConsoleLoginThrottle(clock),
            new WebConsoleOriginPolicy(options.PublicBaseUrl, options.Port),
            diagnostics,
            admin,
            audit,
            assets,
            clock,
            new NullPluginLog());

        return new AcceptanceHarness(options, handler, store, commandRunner, directory, clock);
    }

    public WebConsoleHttpRequest Request(
        string method,
        string path,
        string? body = null,
        string? cookie = null,
        string? csrf = null,
        string? ip = "127.0.0.1")
        => new(
            method,
            path,
            string.Empty,
            BuildHeaders(cookie, csrf),
            body ?? string.Empty,
            ip ?? "127.0.0.1",
            false);

    private Dictionary<string, string> BuildHeaders(string? cookie, string? csrf)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Origin"] = $"http://127.0.0.1:{Options.Port}",
        };
        if (cookie is not null)
            headers["Cookie"] = cookie;
        if (csrf is not null)
            headers[WebConsoleDefaults.CsrfHeaderName] = csrf;
        return headers;
    }

    public async Task<(string? Cookie, string? Csrf, WebConsoleHttpResponse Response)> LoginAsync()
    {
        var response = await Handler.HandleAsync(Request(
            "POST",
            "/api/session",
            JsonSerializer.Serialize(new { token = Token })));

        var cookie = response.Headers.TryGetValue("Set-Cookie", out var setCookie)
            ? setCookie.Split(';')[0]
            : null;

        string? csrf = null;
        if (response.StatusCode == 200)
        {
            using var document = JsonDocument.Parse(response.Body);
            csrf = document.RootElement.GetProperty("csrf").GetString();
        }

        return (cookie, csrf, response);
    }

    public static string BodyText(WebConsoleHttpResponse response)
        => Encoding.UTF8.GetString(response.Body);

    public static JsonElement Json(WebConsoleHttpResponse response)
    {
        using var document = JsonDocument.Parse(response.Body);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
