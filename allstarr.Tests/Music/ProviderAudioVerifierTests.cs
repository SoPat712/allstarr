using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

public sealed class ProviderAudioVerifierTests
{
    [MediaToolsFact]
    public async Task CompleteVerificationRejectsEvenOneMissingFrameFromADeclaredLosslessTimeline()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, PlaybackAudioFixtures.Wave()[..^4]);
            await Assert.ThrowsAsync<IOException>(() => new ProviderAudioVerifier().VerifyFileAsync(path,
                PlaybackAudioFixtures.WaveMedia, null, default));
        }
        finally { File.Delete(path); }
    }

    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovieMetadataAtTheEndIsVerifiedAndTheOriginalBytesArePreserved(bool ranges)
    {
        var directory = Directory.CreateTempSubdirectory("allstarr-movie-check-").FullName;
        try
        {
            var wave = PlaybackAudioFixtures.Wave(24);
            System.Security.Cryptography.RandomNumberGenerator.Fill(wave.AsSpan(44));
            var input = Path.Combine(directory, "input.wav");
            var path = Path.Combine(directory, "original.m4a");
            await File.WriteAllBytesAsync(input, wave);
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-v", "error", "-nostdin", "-threads", "1", "-i", input, "-c:a", "alac", "-threads", "1", path }) start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                await process.WaitForExitAsync();
                await errors;
                Assert.Equal(0, process.ExitCode);
            }
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length > 1024 * 1024);
            using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var calls = 0;
            Task<HttpResponseMessage> Open(long offset, long? end, CancellationToken token)
            {
                calls++;
                var last = end ?? bytes.Length - 1;
                var result = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(bytes[(int)offset..((int)last + 1)]) };
                result.Content.Headers.ContentRange = new(offset, last, bytes.Length);
                return Task.FromResult(result);
            }
            var verified = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
                new("audio/mp4", "mp4", "alac"), 24, default, ranges ? Open : null);
            Assert.True(verified.IsSuccess);
            Assert.Equal(ranges ? 1 : 0, calls);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [MediaToolsFact]
    public async Task CompleteVerificationWaitsForConversionCapacityWithoutRejectingTheOriginal()
    {
        var limits = new MediaCapacityLimits(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [MediaCapacityLimits.ConversionsKey] = "1" }).Build());
        var gate = new MediaConversionGate(limits);
        var occupied = await gate.EnterAsync(default);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, PlaybackAudioFixtures.Wave());
            var verification = new ProviderAudioVerifier(gate).VerifyFileAsync(path, PlaybackAudioFixtures.WaveMedia, 8, default);
            Assert.False(verification.IsCompleted);
            occupied.Dispose();
            Assert.InRange((await verification.WaitAsync(TimeSpan.FromSeconds(10))).DurationSeconds!.Value, 7.99, 8.01);
        }
        finally { occupied.Dispose(); File.Delete(path); }
    }

    [MediaToolsFact]
    public async Task VerifiedPrefixReplaysEveryByteWithoutWaitingForTheTail()
    {
        var bytes = PlaybackAudioFixtures.Wave();
        using var input = new VerificationTailStream(bytes);
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(input) };
        response.Content.Headers.ContentLength = bytes.Length;
        var facts = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
            PlaybackAudioFixtures.WaveMedia, 8, default);
        Assert.True(facts.IsSuccess, facts.Error?.SafeMessage);
        Assert.False(input.Release.Task.IsCompleted);
        Assert.Equal(8, facts.RequireValue().DurationSeconds);
        input.Release.TrySetResult();
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [MediaToolsTheory]
    [InlineData("garbage")]
    [InlineData("wrong-codec")]
    [InlineData("wrong-duration")]
    public async Task PrefixRejectsNonAudioAndContradictoryMediaBeforeDelivery(string failure)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(failure == "garbage" ? new byte[262144] : PlaybackAudioFixtures.Wave()) };
        var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
            failure == "wrong-codec" ? new("audio/flac", "flac", "flac") : PlaybackAudioFixtures.WaveMedia,
            failure == "wrong-duration" ? 30 : 8, default);
        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
    }

    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteVerificationRejectsMissingSamplesEvenWhenContainerDurationLooksComplete(bool truncated)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
        var bytes = PlaybackAudioFixtures.Wave();
        await File.WriteAllBytesAsync(path, truncated ? bytes[..262144] : bytes);
        try
        {
            if (truncated)
            {
                await Assert.ThrowsAsync<IOException>(() => new ProviderAudioVerifier().VerifyFileAsync(path,
                    PlaybackAudioFixtures.WaveMedia, 8, default));
                await Assert.ThrowsAsync<IOException>(() => new ProviderAudioVerifier().VerifyFileAsync(path,
                    PlaybackAudioFixtures.WaveMedia, null, default));
            }
            else
            {
                var facts = await new ProviderAudioVerifier().VerifyFileAsync(path, PlaybackAudioFixtures.WaveMedia, 8, default);
                Assert.Equal(bytes.Length, facts.Size);
                Assert.InRange(facts.DurationSeconds!.Value, 7.99, 8.01);
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class VerificationTailStream(byte[] bytes) : Stream
    {
        private int position;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= 262144) await Release.Task.WaitAsync(cancellationToken);
            var count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class PlaybackAudioFixtures
{
    internal static ProviderMediaFormat WaveMedia { get; } = new("audio/wav", "wav", "pcm_s16le", 512000, 16000, 16, 2);
    internal static byte[] Wave(int seconds = 8)
    {
        var dataLength = seconds * 64000;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8);
        writer.Write(dataLength + 36);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(16000);
        writer.Write(64000);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(Enumerable.Range(0, dataLength).Select(index => (byte)(index * 31)).ToArray());
        return output.ToArray();
    }
}
