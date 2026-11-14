using System.Reflection;
using System.Runtime.InteropServices;

namespace Natter.Cli.Core;

/// <summary>Framework/runtime information shared by CLI commands and diagnostics.</summary>
public static class NatterInfo
{
    public static string Version { get; } = ResolveVersion();

    public static string Runtime => RuntimeInformation.FrameworkDescription;

    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    public static string Os => RuntimeInformation.OSDescription;

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    private static string ResolveVersion()
    {
        var assembly = typeof(NatterInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? assembly.GetName().Version?.ToString()
                            ?? "0.0.0";

        var metadataIndex = informational.IndexOf('+');
        return metadataIndex > 0 ? informational[..metadataIndex] : informational;
    }
}
