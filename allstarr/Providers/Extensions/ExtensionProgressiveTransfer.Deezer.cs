using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using allstarr.Services.Deezer;
using Microsoft.Extensions.Logging;

namespace allstarr.Core.Extensions;

internal sealed class ExtensionDeezerSpeculation(ILogger logger)
{
    private static readonly ConcurrentDictionary<(string Id, string Version), ExtensionDeezerSpeculation> Versions = new();
    private int disabled;
    public bool Enabled => Volatile.Read(ref disabled) == 0;

    public static ExtensionDeezerSpeculation? ForVersion(string id, string version, string script, ILogger logger)
    {
        try
        {
            bool ContainsTransform(Node node) =>
                node is CallExpression
                {
                    Callee: MemberExpression
                    {
                        Object: Identifier { Name: "file" },
                        Computed: false, Property: Identifier { Name: "transformPatternedBlocks" }
                    }
                } ||
                node.ChildNodes.Any(ContainsTransform);
            return ContainsTransform(new Parser().ParseScript(script))
                ? Versions.GetOrAdd((id, version), _ => new(logger)) : null;
        }
        catch { return null; }
    }

    public void Disable(string reason)
    {
        if (Interlocked.Exchange(ref disabled, 1) == 0)
            logger.LogWarning("Progressive patterned decryption disabled until restart: {Reason}", reason);
    }
}

internal sealed partial class ExtensionProgressiveTransfer
{
    private string? deezerTrackId;
    private string? encryptedPath;
    private byte[]? predictedKey;
    private bool transformConfirmed;

    public void PrepareDeezer(string trackId)
    {
        if (speculation?.Enabled != true) return;
        var match = Regex.Match(trackId.Trim(), @"^(?:deezer:)?([0-9]+)$|track/([0-9]+)", RegexOptions.CultureInvariant);
        deezerTrackId = match.Success ? match.Groups[1].Value + match.Groups[2].Value : null;
    }

    public DeezerDownload? StartDeezerDownload(Uri requested, Uri final, string target, long? total)
    {
        static bool IsMedia(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.EndsWith(".dzcdn.net", StringComparison.OrdinalIgnoreCase);
        if (speculation?.Enabled != true || deezerTrackId == null ||
            !Path.GetFileNameWithoutExtension(target).EndsWith(".encrypted", StringComparison.OrdinalIgnoreCase) ||
            !IsMedia(requested) || !IsMedia(final)) return null;
        encryptedPath = target;
        predictedKey = DeezerDecryptedStream.Key(deezerTrackId);
        var plain = target + "." + Guid.NewGuid().ToString("N") + ".stream";
        var download = new DeezerDownload(this, plain, predictedKey);
        Begin(plain, plain, total, null, decrypted: true);
        return download;
    }

    public void CheckTransform(string source, string algorithm, string mode, string padding, string operation,
        byte[] key, byte[] iv, int segmentSize, int every, int offset, bool partial)
    {
        lock (gate)
        {
            if (!Published || encryptedPath == null) return;
            if (source != encryptedPath || algorithm != "blowfish" || mode != "cbc" || padding != "none" ||
                operation != "decrypt" || segmentSize != 2048 || every != 3 || offset != 0 || partial ||
                !iv.AsSpan().SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }) ||
                predictedKey == null || !CryptographicOperations.FixedTimeEquals(key, predictedKey))
            {
                RejectSpeculation("transform parameter mismatch");
                throw new InvalidDataException("The patterned transform differs from the published download.");
            }
        }
    }

    public void ConfirmTransform(string source, string output, string staged, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!Published || encryptedPath == null) return;
            using var stream = File.OpenRead(staged);
            digest ??= hash.GetHashAndReset();
            if (source != encryptedPath || written != stream.Length ||
                !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashDataAsync(stream, cancellationToken).GetAwaiter().GetResult()))
            {
                RejectSpeculation("transform output mismatch");
                throw new InvalidDataException("The patterned transform output differs from the published download.");
            }
            committedPath = output;
            transformConfirmed = true;
        }
    }

    public void TransformFailed()
    {
        lock (gate)
        {
            if (Published && encryptedPath != null) RejectSpeculation("transform failed");
        }
    }

    private void RejectSpeculation(string reason)
    {
        if (encryptedPath != null) speculation?.Disable(reason);
        failed = true;
        Pulse();
    }

    internal sealed class DeezerDownload(ExtensionProgressiveTransfer owner, string path, byte[] key) : IDisposable
    {
        private readonly FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        private readonly byte[] block = new byte[2048];
        private int count;
        private long index;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var length = Math.Min(block.Length - count, bytes.Length);
                bytes[..length].CopyTo(block.AsSpan(count));
                count += length;
                bytes = bytes[length..];
                if (count != block.Length) continue;
                if (index % 3 == 0) DeezerDecryptedStream.Decrypt(block, key);
                index++;
                Write();
            }
        }

        public void Complete() { if (count > 0) Write(); }

        private void Write()
        {
            output.Write(block, 0, count);
            output.Flush();
            owner.Append(block.AsSpan(0, count));
            count = 0;
        }

        public void Dispose() => output.Dispose();
    }
}
