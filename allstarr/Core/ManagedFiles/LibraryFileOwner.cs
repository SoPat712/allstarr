using System.Runtime.InteropServices;

namespace allstarr.Core.ManagedFiles;

// Allstarr usually runs as root in its container. What it adds to a music folder takes the owner of the folder it
// lands in, so the household can still rename or delete those files outside Allstarr.
internal static class LibraryFileOwner
{
    // Directories below root that creating directory would add, outermost first.
    public static string[] Missing(string root, string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var missing = new List<string>();
        for (var current = Path.GetFullPath(directory); current.StartsWith(prefix, StringComparison.Ordinal) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
            missing.Insert(0, current);
        return [.. missing];
    }

    // Only paths Allstarr just created are passed in, outermost first; anything not owned by root is left alone.
    public static void MatchParent(params string[] paths)
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess || paths.Length == 0) return;
        try
        {
            foreach (var path in paths)
                if (TryOwner(path, out var owner) && owner.User == 0 &&
                    TryOwner(Path.GetDirectoryName(path)!, out var parent) && parent != owner)
                    _ = LChown(path, parent.User, parent.Group);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    internal static bool TryOwner(string path, out (uint User, uint Group) owner)
    {
        owner = default;
        if (!OperatingSystem.IsLinux()) return false;
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (StatX(AtCurrentDirectory, path, AtSymlinkNoFollow, StatxUid | StatxGid, buffer) != 0) return false;
            // struct statx has one layout on every Linux architecture: stx_uid at 20, stx_gid at 24.
            owner = (unchecked((uint)Marshal.ReadInt32(buffer, 20)), unchecked((uint)Marshal.ReadInt32(buffer, 24)));
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int AtCurrentDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxUid = 0x8;
    private const uint StatxGid = 0x10;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatX(int directory, string path, int flags, uint mask, IntPtr buffer);

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    internal static extern int LChown(string path, uint owner, uint group);
}
