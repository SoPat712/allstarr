namespace allstarr.Core.ManagedFiles;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

    public bool TryCreateReflink(string destinationPath, string sourcePath)
    {
        var succeeded = false;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                succeeded = CloneFileMac(sourcePath, destinationPath, 0) == 0;
                return succeeded;
            }
            if (!OperatingSystem.IsLinux()) return false;

            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            succeeded = IoctlClone(
                destination.SafeFileHandle.DangerousGetHandle().ToInt32(),
                LinuxFiClone,
                source.SafeFileHandle.DangerousGetHandle().ToInt32()) == 0;
            return succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            // Failed clone syscalls can leave a newly-created empty destination.
            // Placement must be able to continue through the verified copy fallback.
            if (!succeeded && File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity)
    {
        identity = null!;
        try
        {
            if (OperatingSystem.IsWindows()) return TryGetWindowsIdentity(path, out identity);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;

            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                for (var index = 0; index < 512; index++) Marshal.WriteByte(buffer, index, 0);
                if (StatUnix(path, buffer) != 0) return false;
                var device = OperatingSystem.IsMacOS()
                    ? unchecked((uint)Marshal.ReadInt32(buffer, 0)).ToString("x")
                    : unchecked((ulong)Marshal.ReadInt64(buffer, 0)).ToString("x");
                var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8)).ToString("x");
                // Device + inode alone is not a durable identity. Unix filesystems may
                // immediately reuse an inode after unlinking a file, which could make a
                // replacement look like the managed output we originally recorded. The
                // status-change timestamp acts as the inode generation available through
                // portable stat(2) layouts on our supported Unix platforms.
                var changeTimeOffset = OperatingSystem.IsMacOS() ? 64 : 104;
                var changeSeconds = unchecked((ulong)Marshal.ReadInt64(buffer, changeTimeOffset)).ToString("x");
                var changeNanoseconds = unchecked((ulong)Marshal.ReadInt64(buffer, changeTimeOffset + 8)).ToString("x");
                var file = $"{inode}:{changeSeconds}:{changeNanoseconds}";
                var links = OperatingSystem.IsMacOS()
                    ? unchecked((ushort)Marshal.ReadInt16(buffer, 6))
                    : (uint)Math.Min(uint.MaxValue, unchecked((ulong)Marshal.ReadInt64(buffer, 16)));
                identity = new ManagedFileSystemIdentity(device, file, links);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

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

    private const nuint LinuxFiClone = 0x40049409;

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlClone(int descriptor, nuint request, int sourceDescriptor);

    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFileMac(string source, string destination, int flags);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int StatUnix(string path, IntPtr buffer);

    private static bool TryGetWindowsIdentity(string path, out ManagedFileSystemIdentity identity)
    {
        identity = null!;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) return false;
        identity = new ManagedFileSystemIdentity(
            info.VolumeSerialNumber.ToString("x"),
            $"{info.FileIndexHigh:x8}{info.FileIndexLow:x8}:" +
            $"{info.CreationTime.dwHighDateTime:x8}{info.CreationTime.dwLowDateTime:x8}",
            info.NumberOfLinks);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);
}
