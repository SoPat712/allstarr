namespace allstarr.Tests;

internal static class CopiedDataFolderFixture
{
    internal static string ReplaceWithCopy(string data)
    {
        var root = Path.GetFullPath(data);
        var allowed = Path.Combine(Path.GetTempPath(), "allstarr-tests") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
            throw new ArgumentException("Only an isolated temporary database fixture can be copied.");
        var copy = root + "-copy";
        var backup = root + "-original";
        Directory.CreateDirectory(copy);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(copy, Path.GetRelativePath(root, directory)));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(copy, Path.GetRelativePath(root, file)));
        Directory.Move(root, backup);
        Directory.Move(copy, root);
        return backup;
    }
}
