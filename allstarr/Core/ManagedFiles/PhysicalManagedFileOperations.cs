namespace allstarr.Core.ManagedFiles;

using System.Runtime.InteropServices;

public sealed class PhysicalManagedFileOperations : IManagedFileOperations
{
    public bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero);
            return LinkUnix(existingPath, linkPath) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // .NET has no portable reflink API. Platform-specific implementations can replace
    // this service; returning false deliberately advances to the safe copy fallback.
    public bool TryCreateReflink(string destinationPath, string sourcePath) => false;

    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    public void MoveNoReplace(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string existingPath, string newPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);
}
