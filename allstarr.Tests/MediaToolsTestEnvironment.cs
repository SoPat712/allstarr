using System.Runtime.CompilerServices;

namespace allstarr.Tests;

internal static class MediaToolsTestEnvironment
{
    [ModuleInitializer]
    internal static void Configure()
    {
        const string tools = "/opt/allstarr-ci/tools";
        if (!Directory.Exists(tools)) return;
        var directories = Directory.EnumerateFiles(tools, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true
        }).Where(path => Path.GetFileName(path) is "ffmpeg" or "ffprobe")
            .Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator,
            new[] { Environment.GetEnvironmentVariable("PATH") }.Concat(directories)));
    }
}
