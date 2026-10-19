// Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/extensions/src/file_transform.rs.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Extensions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace allstarr.Services.Common;

public partial class ExtensionHostBridge
{
    private const int MaximumTransformReadBytes = 16 * 1024 * 1024;

    public object Transform(
        string input, string output, string optionsJson, Action<long, long>? onProgress)
    {
        string? staged = null;
        byte[]? key = null;
        try
        {
            var scope = RequireFileScope();
            var sourcePath = scope.ResolveTemporaryPath(input);
            var target = scope.ResolveTemporaryPath(output);
            using var document = JsonDocument.Parse(optionsJson);
            var options = document.RootElement;
            string Text(string name, string fallback) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
            int Integer(string name, int fallback) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
            byte[] Decode(string name) => Text(name + "Encoding", "utf8").ToLowerInvariant() switch
            {
                "hex" => Convert.FromHexString(Text(name, "")),
                "base64" => Convert.FromBase64String(Text(name, "")),
                "utf8" => Encoding.UTF8.GetBytes(Text(name, "")),
                _ => throw new ArgumentException("unsupported binary encoding")
            };
            var algorithm = Text("algorithm", "").ToLowerInvariant();
            var mode = Text("mode", "cbc").ToLowerInvariant();
            var padding = Text("padding", "none").ToLowerInvariant();
            if (algorithm.Length == 0) throw new ArgumentException("algorithm is required");
            try { key = Decode("key"); }
            catch (Exception) { throw new ArgumentException("invalid key"); }
            if (key.Length == 0) throw new ArgumentException("key is required");
            byte[] iv;
            try { iv = Decode("iv"); }
            catch (Exception) { throw new ArgumentException("invalid iv"); }
            if (mode is not ("cbc" or "ctr")) throw new ArgumentException($"unsupported block cipher mode: {mode}");
            if (padding != "none") throw new ArgumentException("patterned file transforms only support padding: none");
            var operation = Text("operation", "decrypt").ToLowerInvariant();
            if (operation is not ("decrypt" or "encrypt")) throw new ArgumentException("operation must be decrypt or encrypt");
            var segmentSize = Integer("segmentSize", 0);
            var every = Integer("transformEvery", 1);
            var offset = Integer("transformOffset", 0);
            var bufferSize = Integer("bufferSize", 1 << 20);
            var partial = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("transformPartial", out var partialValue) && partialValue.GetBoolean();
            if (segmentSize is < 1 or > MaximumTransformReadBytes)
                throw new ArgumentException($"segmentSize must be between 1 and {MaximumTransformReadBytes} bytes");
            if (every <= 0) throw new ArgumentException("transformEvery must be greater than zero");
            if (offset < 0 || offset >= every) throw new ArgumentException("transformOffset must be between 0 and transformEvery - 1");
            scope.Progressive?.CheckTransform(sourcePath, algorithm, mode, padding, operation, key, iv, segmentSize, every, offset, partial);
            bufferSize = Math.Clamp(bufferSize, segmentSize, MaximumTransformReadBytes);
            bufferSize -= bufferSize % segmentSize;
            IBlockCipher engine = algorithm switch
            {
                "blowfish" => new BlowfishEngine(),
                "aes" => new AesEngine(),
                _ => throw new ArgumentException("Unsupported transform algorithm.")
            };
            var blockSize = engine.GetBlockSize();
            if (iv.Length != blockSize) throw new ArgumentException($"iv must be {blockSize} bytes for {algorithm}");
            if (mode == "cbc" && segmentSize % blockSize != 0)
                throw new ArgumentException($"segmentSize must be a multiple of {blockSize} bytes for CBC");
            IBlockCipher cipher = mode == "cbc" ? new CbcBlockCipher(engine) : new SicBlockCipher(engine);
            var parameters = new ParametersWithIV(new KeyParameter(key), iv);
            cipher.Init(operation == "encrypt", parameters);
            if (input == output + ".transform.partial")
                throw new ArgumentException("input path conflicts with transform staging path");
            var buffer = new byte[bufferSize];
            long processed = 0, segments = 0, transformed = 0;
            staged = scope.ResolveTemporaryPath(output + "." + Guid.NewGuid().ToString("N") + ".partial");
            using (var source = File.OpenRead(sourcePath))
            using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var total = source.Length;
                if (total > scope.MaximumBytes) throw new InvalidDataException("Transform exceeds the managed artifact size limit.");
                while (true)
                {
                    scope.CancellationToken.ThrowIfCancellationRequested();
                    var count = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (count == 0) break;
                    if (processed + count > scope.MaximumBytes) throw new InvalidDataException("Transform exceeds the managed artifact size limit.");
                    for (var start = 0; start < count; start += segmentSize, segments++)
                    {
                        scope.CancellationToken.ThrowIfCancellationRequested();
                        var length = Math.Min(segmentSize, count - start);
                        if (segments % every != offset || (length != segmentSize && !partial)) continue;
                        if (mode == "cbc" && length % blockSize != 0)
                            throw new ArgumentException($"selected segment {segments} is not a multiple of {blockSize} bytes");
                        cipher.Init(operation == "encrypt", parameters);
                        var block = new byte[blockSize];
                        for (var position = 0; position < length; position += blockSize)
                        {
                            scope.CancellationToken.ThrowIfCancellationRequested();
                            var size = Math.Min(blockSize, length - position);
                            Array.Clear(block);
                            Buffer.BlockCopy(buffer, start + position, block, 0, size);
                            cipher.ProcessBlock(block, 0, block, 0);
                            Buffer.BlockCopy(block, 0, buffer, start + position, size);
                        }
                        transformed++;
                    }
                    destination.Write(buffer, 0, count);
                    processed += count;
                    onProgress?.Invoke(processed, total);
                    scope.CancellationToken.ThrowIfCancellationRequested();
                }
            }
            scope.CancellationToken.ThrowIfCancellationRequested();
            scope.Progressive?.ConfirmTransform(sourcePath, target, staged, scope.CancellationToken);
            File.Move(staged, target, overwrite: true);
            return new { success = true, path = output, bytes_processed = processed, segments_processed = segments, segments_transformed = transformed };
        }
        catch (Exception exception)
        {
            var progressive = ExtensionArtifactInvocationScope.Current?.Progressive;
            if (exception is OperationCanceledException) progressive?.Fail();
            else progressive?.TransformFailed();
            return new
            {
                success = false,
                error = exception switch
                {
                    OperationCanceledException => "Transform cancelled.",
                    ArgumentException => exception.Message,
                    _ => "File transform failed."
                }
            };
        }
        finally
        {
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (staged != null && File.Exists(staged)) File.Delete(staged);
        }
    }
}
