using allstarr.Core.Storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace allstarr.Core.Secrets;

public sealed record SecretKeyRing(string ActiveKeyId, IReadOnlyDictionary<string, byte[]> Keys)
{
    public byte[] GetActiveKey() => GetKey(ActiveKeyId);

    public byte[] GetKey(string keyId) => Keys.TryGetValue(keyId, out var key)
        ? key
        : throw new SecretKeyUnavailableException(keyId);
}

public sealed class SecretKeyUnavailableException(string keyId)
    : InvalidOperationException($"Encryption key '{keyId}' is unavailable.");

public sealed class FileSecretKeyRingProvider
{
    // File.Move without overwrite checks then renames on Unix, so concurrent creators must be serialized.
    private static readonly KeyedAsyncLock CreationLocks = new();
    private readonly SecretStoreOptions _options;

    public FileSecretKeyRingProvider(SecretStoreOptions options)
    {
        _options = options;
    }

    public async Task<bool> CreateIfMissingAsync(bool hasEncryptedSecrets, CancellationToken cancellationToken = default)
    {
        _options.Validate();
        var path = Path.GetFullPath(_options.KeyRingPath);
        if (File.Exists(path)) return false;
        await using var creation = await CreationLocks.AcquireAsync(path, cancellationToken);
        if (File.Exists(path)) return false;
        if (hasEncryptedSecrets)
            throw new FileNotFoundException("Encrypted secrets exist; restore the original encryption key ring.", path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        var key = RandomNumberGenerator.GetBytes(32);
        var createdTemporary = false;
        try
        {
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, fileOptions))
            {
                createdTemporary = true;
                var id = Guid.NewGuid().ToString("N");
                await JsonSerializer.SerializeAsync(stream, new
                {
                    activeKeyId = id,
                    keys = new Dictionary<string, string> { [id] = Convert.ToBase64String(key) }
                }, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (createdTemporary) File.Delete(temporary);
        }
    }

    public async Task<SecretKeyRing> LoadAsync(CancellationToken cancellationToken = default)
    {
        _options.Validate();
        var path = Path.GetFullPath(_options.KeyRingPath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The configured secret key ring is not available.",
                path);
        }

        ValidateUnixPermissions(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<KeyRingDocument>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
        if (document == null || string.IsNullOrWhiteSpace(document.ActiveKeyId))
        {
            throw new InvalidOperationException("The secret key ring has no activeKeyId.");
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (keyId, encoded) in document.Keys)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"Secret key '{keyId}' is not valid base64.");
            }

            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException(
                    $"Secret key '{keyId}' must decode to exactly 32 bytes.");
            }

            keys.Add(keyId, key);
        }

        if (!keys.ContainsKey(document.ActiveKeyId))
        {
            ClearKeys(keys.Values);
            throw new InvalidOperationException("The active secret key is not present in the key ring.");
        }

        return new SecretKeyRing(document.ActiveKeyId, keys);
    }

    private static void ValidateUnixPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode forbidden =
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite;
        if ((mode & forbidden) != 0)
        {
            throw new InvalidOperationException(
                "The secret key ring must not be readable or writable by group/other users.");
        }
    }

    private static void ClearKeys(IEnumerable<byte[]> keys)
    {
        foreach (var key in keys)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed class KeyRingDocument
    {
        public string ActiveKeyId { get; set; } = string.Empty;
        public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
    }
}
