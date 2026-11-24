using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Infrastructure.Composition;
using Natter.Plugin.Abstractions;
using Natter.Plugin.Store;
using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// S2 batch (docs/CODE_REVIEW_2026-11-22.md §4.3): package validation hardening, store atomicity and
/// plugin lifecycle recovery.
/// </summary>
public sealed class PluginPackageSecurityTests
{
    private const string EntryType = "Natter.Sample.Plugin.Greetings.GreetingsNpsPlugin";
    private const string DefaultEntryPath = "lib/Natter.Sample.Plugin.Greetings.dll";

    // ---------------------------------------------------------------- S2-C1 zip bomb

    [Fact]
    public async Task Zip_bomb_packages_are_rejected_and_never_land_on_disk()
    {
        using var temp = new TempDirectory();
        var payload = new byte[8 * 1024 * 1024]; // zeros: compresses to a few KiB, expands to 8 MiB

        var options = new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "linux",
            AllowUnsigned = true,
            MaxTotalUncompressedBytes = 1024 * 1024,
            MaxEntryUncompressedBytes = 1024 * 1024,
        };

        // (a) honest declarations: the pre-check refuses the package before extraction.
        var honest = PluginPackageTestBuilder.Create(
            temp.Combine("bomb-honest"), entryBytes: payload, sign: false, omitSignature: true);
        var honestResult = await new PluginPackageValidator(options)
            .ValidateAsync(honest.PackagePath, temp.Combine("extract-honest"));
        Assert.False(honestResult.IsValid);
        AssertBoundedExtraction(temp.Combine("extract-honest"));

        // (b) lying declarations (bomb): the declared sizes claim one byte while the entry is 8 MiB.
        // .NET's reader honours the declaration, so the payload is truncated; the per-byte accounting
        // in ExtractAsync is the backstop that keeps a non-conforming reader from filling the disk.
        var lying = PluginPackageTestBuilder.Create(
            temp.Combine("bomb-lying"), entryBytes: payload, sign: false, omitSignature: true);
        PatchDeclaredUncompressedSize(lying.PackagePath, DefaultEntryPath, 1);

        var lyingResult = await new PluginPackageValidator(options)
            .ValidateAsync(lying.PackagePath, temp.Combine("extract-lying"));

        Assert.False(lyingResult.IsValid);
        AssertBoundedExtraction(temp.Combine("extract-lying"));
    }

    private static void AssertBoundedExtraction(string extractRoot)
    {
        if (!Directory.Exists(extractRoot))
            return;

        var biggest = Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file).Length)
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(biggest <= 2 * 1024 * 1024, $"extraction wrote {biggest} bytes for an 8 MiB bomb");
    }

    // ---------------------------------------------------------------- S2-C8 signature size

    [Fact]
    public async Task Oversized_signature_file_is_rejected_before_loading_it()
    {
        using var temp = new TempDirectory();
        var package = PluginPackageTestBuilder.Create(
            temp.Combine("bigsig"),
            sign: false,
            omitSignature: true,
            additionalFiles: new Dictionary<string, byte[]>
            {
                [PluginPackageConstants.SignatureFile] = new byte[PluginPackageValidator.MaxSignatureBytes + 1024],
            });

        var validator = new PluginPackageValidator(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins"),
            Platform = "linux",
            AllowUnsigned = false,
            TrustedPublicKeys = [Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo())],
        });

        var result = await validator.ValidateAsync(package.PackagePath, temp.Combine("extract"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "signature-too-large");
    }

    // ---------------------------------------------------------------- S2-C11 entry containment

    [Fact]
    public async Task Entry_assembly_outside_the_package_is_rejected()
    {
        using var temp = new TempDirectory();
        var manifest = PluginPackageTestBuilder.BuildManifest(entryPath: "../outside.dll");
        var package = PluginPackageTestBuilder.Create(temp.Combine("escape"), manifestJson: manifest, sign: false, omitSignature: true);

        var result = await ValidateAsync(temp, package.PackagePath, allowUnsigned: true);

        Assert.False(result.IsValid);
        // The manifest reader already rejects the escaping path; the validator containment check is
        // the second line of defence.
        Assert.Contains(result.Issues, issue => issue.Code is
            "entry-outside-package" or "entry-missing" or "manifest-invalid-path");
    }

    // ---------------------------------------------------------------- S2-C13 native magic

    [Fact]
    public async Task Elf_and_macho_images_are_rejected_regardless_of_extension()
    {
        using var temp = new TempDirectory();

        var elf = new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0 };
        var machO = new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 1, 0, 0, 0, 0, 0, 0, 0 };

        var package = PluginPackageTestBuilder.Create(
            temp.Combine("native"),
            sign: false,
            omitSignature: true,
            additionalFiles: new Dictionary<string, byte[]>
            {
                ["assets/readme.txt"] = elf,
                ["assets/theme.dat"] = machO,
            });

        var result = await ValidateAsync(temp, package.PackagePath, allowUnsigned: true);

        Assert.False(result.IsValid);
        var nativeIssues = result.Issues.Where(issue => issue.Code == "native-library").ToArray();
        Assert.Equal(2, nativeIssues.Length);
        Assert.Contains(nativeIssues, issue => issue.Path == "assets/readme.txt");
        Assert.Contains(nativeIssues, issue => issue.Path == "assets/theme.dat");
    }

    // ---------------------------------------------------------------- S2-C6 constant time

    [Fact]
    public void Hash_comparison_is_constant_time_and_case_insensitive()
    {
        const string hash = "9f2c1d0b7a4e6f8c3d5b1a0e9f8c7b6a5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f00";

        Assert.True(PluginPackageSignatureVerifier.HashesMatch(hash, hash));
        Assert.True(PluginPackageSignatureVerifier.HashesMatch(hash.ToUpperInvariant(), hash));
        Assert.False(PluginPackageSignatureVerifier.HashesMatch(hash, hash[..^1] + "1"));
        Assert.False(PluginPackageSignatureVerifier.HashesMatch(hash, null));
        Assert.False(PluginPackageSignatureVerifier.HashesMatch(null, hash));
    }

    // ---------------------------------------------------------------- S2-C7 canonical text

    [Fact]
    public void Canonical_v2_text_is_unambiguous_for_odd_file_names()
    {
        var files = new (string Path, string Hash)[]
        {
            ("lib/a.dll", new string('a', 64)),
            ("lib/b\nc.dll", new string('b', 64)),
        };

        var v2 = PluginPackageSignatureVerifier.BuildCanonicalText(files);
        var legacy = PluginPackageSignatureVerifier.BuildLegacyCanonicalText(files);

        Assert.Equal(2 + files.Length * 2, v2.Split('\n').Length - 1); // header + count + path/hash per file
        Assert.Contains("%0A", v2, StringComparison.Ordinal); // newline escaped, boundaries preserved
        Assert.DoesNotContain("lib/b\nc.dll", v2, StringComparison.Ordinal);
        Assert.Contains("lib/b\nc.dll", legacy, StringComparison.Ordinal);

        // Length prefixes round-trip: the same file set always produces byte-identical text.
        Assert.Equal(v2, PluginPackageSignatureVerifier.BuildCanonicalText(files));
    }

    [Fact]
    public async Task Legacy_signature_format_is_reported_and_can_be_rejected()
    {
        using var temp = new TempDirectory();

        // The test fixture signs the legacy canonical text, exactly like an already-published package.
        var package = PluginPackageTestBuilder.Create(temp.Combine("legacy"));

        var lenient = await ValidateAsync(temp, package.PackagePath, trustedKey: package.PublicKey);
        Assert.True(lenient.IsValid);
        Assert.Contains(lenient.WarningList, issue => issue.Code == "signature-legacy-format");
        Assert.DoesNotContain(lenient.Issues, issue => issue.Code == "signature-legacy-format");

        var strict = await ValidateAsync(temp, package.PackagePath, trustedKey: package.PublicKey, rejectLegacy: true);
        Assert.False(strict.IsValid);
        Assert.Contains(strict.Issues, issue => issue.Code == "signature-legacy-format");
    }

    // ---------------------------------------------------------------- S2-C5 install atomicity

    [Fact]
    public async Task Failed_install_leaves_no_directory_and_no_dangling_record()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, allowUnsigned: true);
        var package = PluginPackageTestBuilder.Create(temp.Combine("pkg"), sign: false, omitSignature: true);

        // Block the state commit: installed.json is a directory, so the atomic move fails.
        var blocker = store.StatePath;
        Directory.CreateDirectory(blocker);

        await Assert.ThrowsAnyAsync<Exception>(() => store.InstallAsync(package.PackagePath));

        var versionPath = Path.Combine(store.RootDirectory, package.PluginId, package.Version);
        Assert.False(Directory.Exists(versionPath), "half-installed version directory must be removed");
        Assert.True(Directory.Exists(blocker), "the blocker itself is untouched");

        Directory.Delete(blocker);

        // The store is usable again and never advertises a missing directory.
        var result = await store.InstallAsync(package.PackagePath);
        Assert.Equal(package.Version, result.Record.ActiveVersion);
        foreach (var record in store.GetInstalledPlugins())
        {
            var active = Path.Combine(store.RootDirectory, record.Id, record.ActiveVersion);
            Assert.True(Directory.Exists(active), $"state points at missing directory '{active}'");
        }
    }

    // ---------------------------------------------------------------- S2-C15 corrupt state

    [Fact]
    public async Task Corrupt_state_is_backed_up_and_salvaged_instead_of_bricking_the_store()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, allowUnsigned: true);
        var package = PluginPackageTestBuilder.Create(temp.Combine("pkg"), sign: false, omitSignature: true);
        await store.InstallAsync(package.PackagePath);

        // One complete record followed by a truncated one.
        File.WriteAllText(store.StatePath,
            "{\"schemaVersion\":1,\"plugins\":["
            + "{\"id\":\"" + package.PluginId + "\",\"name\":\"Test Plugin\",\"version\":\"" + package.Version
            + "\",\"activeVersion\":\"" + package.Version + "\",\"enabled\":true,\"trust\":\"unsigned\"},"
            + "{\"id\":\"broken.plugin\",\"name\":");

        var records = store.GetInstalledPlugins();

        Assert.Single(records);
        Assert.Equal(package.PluginId, records[0].Id);
        Assert.Contains(store.Warnings, warning => warning.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(Directory.GetFiles(store.RootDirectory, "installed.json.corrupt-*"));

        // The repaired document is readable and still points at an existing directory.
        var repaired = File.ReadAllText(store.StatePath);
        Assert.Contains(package.PluginId, repaired, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(store.RootDirectory, records[0].Id, records[0].ActiveVersion)));
    }

    // ---------------------------------------------------------------- S2-C10 cleanup lock

    [Fact]
    public async Task Cleanup_respects_the_store_lock_and_the_gate()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp, allowUnsigned: true);
        var staged = Path.Combine(store.StagingDirectory, "txn-locked", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllTextAsync(staged, "staged");

        using var lockStream = new FileStream(
            Path.Combine(store.RootDirectory, ".store.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CleanupAsync(cancellation.Token));
        Assert.True(File.Exists(staged), "cleanup deleted staging while another process holds the store lock");

        lockStream.Dispose();
        await store.CleanupAsync();
        Assert.False(File.Exists(staged));
    }

    // ---------------------------------------------------------------- S2-C4 dependency cycles

    [Fact]
    public async Task Cycle_fails_only_its_members_and_names_them()
    {
        var a = CreatePlainPluginDirectory("natter.cycle.a", "natter.cycle.b");
        var b = CreatePlainPluginDirectory("natter.cycle.b", "natter.cycle.a");
        var standalone = CreatePlainPluginDirectory("natter.cycle.standalone", dependsOn: null);

        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(a)
                .AddPluginDirectory(b)
                .AddPluginDirectory(standalone);

            await using var host = builder.Build();
            await host.StartAsync();

            var plugins = host.Services.GetRequiredService<IPluginCatalog>().Plugins
                .ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);

            Assert.Equal(PluginState.Activated, plugins["natter.cycle.standalone"].State);

            foreach (var id in new[] { "natter.cycle.a", "natter.cycle.b" })
            {
                // Graph errors surface as Blocked (the candidate itself is never loaded).
                Assert.Equal(PluginState.Blocked, plugins[id].State);
                Assert.Equal("dependency-cycle", plugins[id].ErrorDetail!.Code);
                Assert.Contains("natter.cycle.a", plugins[id].Error!, StringComparison.Ordinal);
                Assert.Contains("natter.cycle.b", plugins[id].Error!, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var directory in new[] { a, b, standalone })
                TryDelete(directory);
        }
    }

    // ---------------------------------------------------------------- S2-C3 unload

    [Fact]
    public async Task Unloading_releases_the_collectible_load_context()
    {
        var directory = CreatePlainPluginDirectory("natter.unload.greetings", dependsOn: null);
        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            var host = builder.Build();
            await host.StartAsync();

            var weak = CapturePluginTypeReference(host.Services.GetRequiredService<IGreetingPort>());

            await host.StopAsync();
            await host.DisposeAsync();

            // Drop the host/provider itself: any singleton cache would otherwise root the plugin
            // instance for the rest of the test method.
            host = null!;
            builder = null!;

            Assert.True(WaitForCollection(weak), "plugin assembly/type stayed alive after unload");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    // ---------------------------------------------------------------- default configuration

    [Fact]
    public async Task Default_configuration_still_loads_a_plain_plugin_directory()
    {
        var directory = CreatePlainPluginDirectory("natter.regression.greetings", dependsOn: null);
        try
        {
            var builder = NatterApplicationBuilder.Create()
                .AddNatterFramework()
                .AddPluginDirectory(directory);

            await using var host = builder.Build();
            await host.StartAsync();

            var descriptor = Assert.Single(host.Services.GetRequiredService<IPluginCatalog>().Plugins);
            Assert.Equal(PluginState.Activated, descriptor.State);
            Assert.Contains("cli.commands", descriptor.GrantedPermissions ?? [], StringComparer.Ordinal);
        }
        finally
        {
            TryDelete(directory);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<PluginPackageValidationResult> ValidateAsync(
        TempDirectory temp,
        string packagePath,
        bool allowUnsigned = false,
        string? trustedKey = null,
        bool rejectLegacy = false)
    {
        var options = new PluginStoreOptions
        {
            RootDirectory = temp.Combine("plugins-" + Guid.NewGuid().ToString("N")[..8]),
            Platform = "linux",
            AllowUnsigned = allowUnsigned,
            TrustedPublicKeys = trustedKey is null ? [] : [trustedKey],
            RejectLegacySignatureFormat = rejectLegacy,
        };

        return await new PluginPackageValidator(options)
            .ValidateAsync(packagePath, temp.Combine("extract-" + Guid.NewGuid().ToString("N")[..8]));
    }

    private static PluginStore CreateStore(TempDirectory temp, bool allowUnsigned)
        => new(new PluginStoreOptions
        {
            RootDirectory = temp.Combine("store-" + Guid.NewGuid().ToString("N")[..8]),
            Platform = "linux",
            AllowUnsigned = allowUnsigned,
        });

    /// <summary>
    /// Rewrites the declared uncompressed size of one entry in both its local and central header, so
    /// the package claims to expand to a handful of bytes while the deflate stream carries megabytes.
    /// </summary>
    private static void PatchDeclaredUncompressedSize(string zipPath, string entryName, uint declared)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var nameBytes = Encoding.UTF8.GetBytes(entryName.Replace('\\', '/'));
        var patched = 0;

        for (var index = 0; index + 4 <= bytes.Length; index++)
        {
            var isLocal = bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 3 && bytes[index + 3] == 4;
            var isCentral = bytes[index] == 0x50 && bytes[index + 1] == 0x4B && bytes[index + 2] == 1 && bytes[index + 3] == 2;
            if (!isLocal && !isCentral)
                continue;

            var nameLengthOffset = isLocal ? 26 : 28;
            var nameOffset = isLocal ? 30 : 46;
            var sizeOffset = index + (isLocal ? 22 : 24);

            if (index + nameOffset + nameBytes.Length >= bytes.Length)
                continue;

            var nameLength = BitConverter.ToUInt16(bytes, index + nameLengthOffset);
            var name = Encoding.UTF8.GetString(bytes, index + nameOffset, Math.Min(nameLength, nameBytes.Length));
            if (!string.Equals(name, entryName.Replace('\\', '/'), StringComparison.Ordinal))
                continue;

            BitConverter.GetBytes(declared).CopyTo(bytes, sizeOffset);
            patched++;
        }

        Assert.True(patched >= 2, $"expected to patch the local and central header of '{entryName}', patched {patched}");
        File.WriteAllBytes(zipPath, bytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CapturePluginTypeReference(object port)
        => new(port.GetType());

    private static bool WaitForCollection(WeakReference reference)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (!reference.IsAlive)
                return true;

            Thread.Sleep(50);
        }

        return !reference.IsAlive;
    }

    private static string CreatePlainPluginDirectory(string pluginId, string? dependsOn)
    {
        var directory = Path.Combine(Path.GetTempPath(), "natter-pkgsec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var assembly = Path.Combine(AppContext.BaseDirectory, "plugins", "greetings", "Natter.Sample.Plugin.Greetings.dll");
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)));

        var dependency = dependsOn is null
            ? string.Empty
            : $$""", "dependsOn": [{ "id": "{{dependsOn}}", "version": ">=0.1.0" }]""";

        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "{{pluginId}}",
              "name": "Security Test {{pluginId}}",
              "version": "0.1.0",
              "kind": "feature",
              "hostApi": ">=0.1.0 <1.0.0",
              "entry": { "assembly": "{{Path.GetFileName(assembly)}}", "type": "{{EntryType}}" },
              "permissions": ["cli.commands"],
              "contributions": [
                { "type": "port.adapter", "id": "greeting" },
                { "type": "cli.command", "id": "greet" },
                { "type": "cli.command", "id": "greetings.recent" }
              ]{{dependency}}
            }
            """);

        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "natter-pkgsec-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

        public void Dispose() => TryDelete(Path);
    }
}
