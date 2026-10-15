using System.Security.Cryptography;
using allstarr.Services.Common;

namespace allstarr.Core.Configuration;

public sealed class MountedLegacyEnvironment(IHostEnvironment environment)
{
    public bool IsAvailable
    {
        get
        {
            try { return Inspect().Length is > 0 and <= LegacyEnvParser.MaxBytes; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public async Task<byte[]> ReadAsync(CancellationToken token = default)
    {
        var info = Inspect();
        if (!info.Exists || info.Length is <= 0 or > LegacyEnvParser.MaxBytes)
            throw new LegacyEnvMigrationException("mounted_env_unavailable", "No bounded legacy environment file is mounted. Upload your saved .env file instead.");
        var bytes = new byte[LegacyEnvParser.MaxBytes + 1];
        try
        {
            await using var input = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(count), token);
                if (read == 0) break;
                count += read;
            }
            _ = Inspect();
            if (count is <= 0 or > LegacyEnvParser.MaxBytes)
                throw new LegacyEnvMigrationException("mounted_env_unavailable", "The mounted environment changed or exceeds the import limit. Upload a saved .env file instead.");
            return bytes.AsSpan(0, count).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private FileInfo Inspect()
    {
        var path = RuntimeEnvConfiguration.ResolveEnvFilePath(environment);
        var info = new FileInfo(path);
        if (info.LinkTarget != null) throw new UnauthorizedAccessException("The mounted environment must be a regular file.");
        for (var directory = info.Directory; directory != null; directory = directory.Parent)
            if (directory.LinkTarget != null) throw new UnauthorizedAccessException("The mounted environment cannot traverse symbolic links.");
        return info;
    }
}
