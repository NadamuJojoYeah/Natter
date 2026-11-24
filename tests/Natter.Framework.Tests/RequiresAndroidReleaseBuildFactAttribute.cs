using Xunit;

namespace Natter.Framework.Tests;

/// <summary>
/// Runs only when a Release build of the Android host exists on disk.
///
/// The Android project needs the `android` workload plus SDK/JDK. CI gates that job behind the
/// ANDROID_BUILD repository variable and excludes the project from the ordinary build/test job, so a
/// normal PR checkout never contains the generated manifest this acceptance inspects. Failing there
/// would be a false negative: the acceptance is about the generated manifest, and there is no
/// generated manifest to inspect.
/// </summary>
public sealed class RequiresAndroidReleaseBuildFactAttribute : FactAttribute
{
    public RequiresAndroidReleaseBuildFactAttribute()
    {
        if (!HasGeneratedReleaseManifest())
        {
            Skip = "no Release build of Natter.Hosting.Android on this machine; set ANDROID_BUILD=true "
                + "in CI or run `dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c Release`.";
        }
    }

    private static bool HasGeneratedReleaseManifest()
    {
        var root = FindRepositoryRoot();
        if (root is null)
            return false;

        var androidRoot = Path.Combine(root, "src", "Natter.Hosting.Android");
        if (!Directory.Exists(androidRoot))
            return false;

        return Directory
            .EnumerateFiles(androidRoot, "AndroidManifest.xml", SearchOption.AllDirectories)
            .Any(path => path.Contains(Path.Combine("obj", "Release"), StringComparison.Ordinal));
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Natter.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
