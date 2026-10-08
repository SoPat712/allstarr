using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace allstarr.Services.Deezer;

internal sealed class DeezerDecryptedStream(
    Stream input,
    string trackId,
    bool leaveOpen = false) : Stream
{
    private const int ChunkSize = 2048;
    private const string BlowfishSecret = "g4el58wc0zvf9na1";
    private static readonly byte[] Iv = [0, 1, 2, 3, 4, 5, 6, 7];
    private readonly byte[] buffer = new byte[ChunkSize];
    private readonly byte[] key = Key(trackId);
    private int bufferOffset;
    private int bufferCount;
    private int chunkIndex;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] destination, int offset, int count) =>
        Read(destination.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (destination.Length == 0) return 0;
        if (bufferOffset == bufferCount && Fill() == 0) return 0;
        var count = Math.Min(destination.Length, bufferCount - bufferOffset);
        buffer.AsSpan(bufferOffset, count).CopyTo(destination);
        bufferOffset += count;
        return count;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0) return 0;
        if (bufferOffset == bufferCount && await FillAsync(cancellationToken) == 0) return 0;
        var count = Math.Min(destination.Length, bufferCount - bufferOffset);
        buffer.AsMemory(bufferOffset, count).CopyTo(destination);
        bufferOffset += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) input.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await input.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private int Fill()
    {
        bufferCount = 0;
        while (bufferCount < buffer.Length)
        {
            var read = input.Read(buffer, bufferCount, buffer.Length - bufferCount);
            if (read == 0) break;
            bufferCount += read;
        }
        return Ready();
    }

    private async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        bufferCount = 0;
        while (bufferCount < buffer.Length)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(bufferCount, buffer.Length - bufferCount), cancellationToken);
            if (read == 0) break;
            bufferCount += read;
        }
        return Ready();
    }

    private int Ready()
    {
        bufferOffset = 0;
        if (bufferCount == ChunkSize && chunkIndex % 3 == 0) Decrypt(buffer, key);
        if (bufferCount > 0) chunkIndex++;
        return bufferCount;
    }

    private static byte[] Key(string trackId)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(trackId))).ToLowerInvariant();
        return Enumerable.Range(0, 16)
            .Select(index => (byte)(hash[index] ^ hash[index + 16] ^ BlowfishSecret[index]))
            .ToArray();
    }

    private static void Decrypt(byte[] data, byte[] key)
    {
        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), Iv));
        for (var offset = 0; offset < data.Length; offset += cipher.GetBlockSize())
            cipher.ProcessBlock(data, offset, data, offset);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
