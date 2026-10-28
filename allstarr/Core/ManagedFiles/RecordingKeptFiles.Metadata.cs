using System.Text.RegularExpressions;

namespace allstarr.Core.ManagedFiles;

public sealed partial class RecordingKeptFiles
{
    private static string KeptPathTemplate(string template, int? track) => track.HasValue
        ? template
        : Regex.Replace(template, @"(^|[/\\])\{track(?::[^{}]+)?\}\s*-\s*", "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
