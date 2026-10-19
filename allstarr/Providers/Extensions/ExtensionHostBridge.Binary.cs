using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Paddings;

namespace allstarr.Services.Common;

public partial class ExtensionHostBridge
{
    private static string BinaryOption(JsonElement options, string name, string fallback) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value)
            ? value.GetString() ?? fallback : fallback;

    private static long BinaryInteger(JsonElement options, string name, long fallback) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value)
            ? value.GetInt64() : fallback;

    private static bool BinaryFlag(JsonElement options, string name) =>
        options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) && value.GetBoolean();

    private static byte[] DecodeBinary(string value, string encoding) => encoding.Trim().ToLowerInvariant() switch
    {
        "hex" => Convert.FromHexString(value),
        "base64" => Convert.FromBase64String(value),
        "utf8" or "utf-8" => Encoding.UTF8.GetBytes(value),
        _ => throw new ArgumentException("unsupported binary encoding")
    };

    private static string EncodeBinary(byte[] value, string encoding) => encoding.Trim().ToLowerInvariant() switch
    {
        "hex" => Convert.ToHexString(value).ToLowerInvariant(),
        "base64" => Convert.ToBase64String(value),
        "utf8" or "utf-8" => Encoding.UTF8.GetString(value),
        _ => throw new ArgumentException("unsupported binary encoding")
    };

    public object FileStat(string path)
    {
        try
        {
            var resolved = RequireFileScope().ResolveTemporaryPath(path);
            return new { success = true, size = new FileInfo(resolved).Length };
        }
        catch (Exception) { return new { success = false, error = "File size is unavailable." }; }
    }

    // Behaviour matches SpotiFLAC-Module-Version (MIT).
    public object FileReadRange(string path, string optionsJson)
    {
        try
        {
            var scope = RequireFileScope();
            using var document = JsonDocument.Parse(optionsJson);
            var options = document.RootElement;
            var offset = BinaryInteger(options, "offset", 0);
            var requested = BinaryInteger(options, "length", -1);
            if (offset < 0) throw new ArgumentException("invalid read range");
            using var input = File.OpenRead(scope.ResolveTemporaryPath(path));
            if (input.Length > scope.MaximumBytes) throw new InvalidDataException("File exceeds the managed artifact size limit.");
            offset = Math.Min(offset, input.Length);
            var available = Math.Max(0, input.Length - offset);
            var length = requested < 0 ? available : requested;
            if (length > Math.Min(scope.MaximumBytes, MaximumTransformReadBytes))
                throw new InvalidDataException("Read exceeds the byte limit.");
            input.Position = offset;
            var bytes = new byte[(int)Math.Min(length, available)];
            scope.CancellationToken.ThrowIfCancellationRequested();
            var count = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count != bytes.Length) Array.Resize(ref bytes, count);
            return new
            {
                success = true,
                data = EncodeBinary(bytes, BinaryOption(options, "encoding", "base64")),
                bytes_read = count,
                offset,
                size = input.Length,
                eof = offset + count >= input.Length
            };
        }
        catch (Exception exception)
        {
            return new { success = false, error = exception is ArgumentException ? exception.Message : "File read failed." };
        }
    }

    public object FileWriteEncoded(string path, string data, string optionsJson)
    {
        try
        {
            var scope = RequireFileScope();
            var resolved = scope.ResolveTemporaryPath(path);
            using var document = JsonDocument.Parse(optionsJson);
            var options = document.RootElement;
            var bytes = DecodeBinary(data, BinaryOption(options, "encoding", "base64"));
            var append = BinaryFlag(options, "append");
            var hasOffset = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("offset", out _);
            var offset = BinaryInteger(options, "offset", 0);
            if (offset < 0 || append && hasOffset) throw new ArgumentException("invalid write offset");
            var truncate = BinaryFlag(options, "truncate");
            var existing = File.Exists(resolved) && !truncate ? new FileInfo(resolved).Length : 0;
            var position = append ? existing : offset;
            if (bytes.LongLength > scope.MaximumBytes || position > scope.MaximumBytes - bytes.LongLength || existing > scope.MaximumBytes)
                throw new InvalidDataException("File exceeds the managed artifact size limit.");
            scope.CancellationToken.ThrowIfCancellationRequested();
            using var output = new FileStream(resolved, truncate ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write);
            output.Position = position;
            output.Write(bytes);
            return new { success = true, path, bytes_written = bytes.Length, size = output.Length };
        }
        catch (Exception exception)
        {
            return new { success = false, error = exception is ArgumentException ? exception.Message : "File write failed." };
        }
    }

    public object DecryptBlockCipher(string data, string optionsJson)
    {
        byte[]? key = null;
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            var options = document.RootElement;
            string Text(string name, string fallback) => BinaryOption(options, name, fallback).ToLowerInvariant();
            var algorithm = Text("algorithm", "");
            key = DecodeBinary(BinaryOption(options, "key", ""), Text("keyEncoding", "utf8"));
            var iv = DecodeBinary(BinaryOption(options, "iv", ""), Text("ivEncoding", "utf8"));
            var bytes = DecodeBinary(data, Text("inputEncoding", "base64"));
            if (bytes.Length > MaximumTransformReadBytes) throw new ArgumentException("Input exceeds the byte limit.");
            if (key.Length == 0) throw new ArgumentException("key is required");
            if (algorithm == "blowfish" && key.Length < 4)
            {
                var shortKey = key;
                key = Enumerable.Range(0, ((4 + shortKey.Length - 1) / shortKey.Length) * shortKey.Length)
                    .Select(index => shortKey[index % shortKey.Length]).ToArray();
                CryptographicOperations.ZeroMemory(shortKey);
            }
            IBlockCipher engine = algorithm switch
            {
                "blowfish" => new BlowfishEngine(),
                "aes" => new AesEngine(),
                _ => throw new ArgumentException("unsupported block cipher algorithm")
            };
            var mode = Text("mode", "cbc");
            var padding = Text("padding", "none");
            if (iv.Length != engine.GetBlockSize()) throw new ArgumentException("invalid iv length");
            IBlockCipherMode cipher = mode switch
            {
                "cbc" => new CbcBlockCipher(engine),
                "ctr" => new SicBlockCipher(engine),
                _ => throw new ArgumentException("unsupported block cipher mode")
            };
            if (padding is not ("none" or "pkcs7")) throw new ArgumentException("unsupported padding");
            BufferedBlockCipher buffered = mode == "cbc" && padding == "pkcs7"
                ? new PaddedBufferedBlockCipher(cipher) : new BufferedBlockCipher(cipher);
            buffered.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
            ExtensionInvocationBudget.Current?.Token.ThrowIfCancellationRequested();
            var result = buffered.DoFinal(bytes);
            return new { success = true, data = EncodeBinary(result, Text("outputEncoding", "base64")), block_size = engine.GetBlockSize() };
        }
        catch (Exception) { return new { success = false, error = "Block decryption failed." }; }
        finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
    }

    // Behaviour matches SpotiFLAC-Module-Version (MIT).
    public object LocalTime()
    {
        var now = DateTimeOffset.Now;
        return new
        {
            year = now.Year,
            month = now.Month,
            day = now.Day,
            hour = now.Hour,
            minute = now.Minute,
            second = now.Second,
            weekday = (int)now.DayOfWeek,
            offsetMinutes = -(int)now.Offset.TotalMinutes,
            timezone = TimeZoneInfo.Local.Id,
            timestamp = now.ToUnixTimeSeconds()
        };
    }
}
