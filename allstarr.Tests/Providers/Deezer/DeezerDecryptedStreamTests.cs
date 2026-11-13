using allstarr.Services.Deezer;

namespace allstarr.Tests;

public sealed class DeezerDecryptedStreamTests
{
    [Fact]
    public void ReadingManyChunksDoesNotAllocatePerChunk()
    {
        using var input = new MemoryStream(new byte[2048 * 300]);
        using var stream = new DeezerDecryptedStream(input, "42");
        var buffer = new byte[2048];
        stream.ReadExactly(buffer);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        int read;
        while ((read = stream.Read(buffer)) != 0) total += read;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2048 * 299, total);
        Assert.True(allocated < 16 * 1024, $"Steady-state decryption allocated {allocated} bytes.");
    }
}
