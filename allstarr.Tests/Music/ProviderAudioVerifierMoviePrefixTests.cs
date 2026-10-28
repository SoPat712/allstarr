using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class ProviderAudioVerifierMoviePrefixTests
{
    [MediaToolsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeFrontArtworkLeavesAudioVerifiableWithoutDrainingTheOriginal(bool partial)
    {
        var bytes = await MovieAsync();
        using var input = new GatedStream(bytes);
        using var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = partial ? new ByteArrayContent(bytes[..65536]) : new StreamContent(input) };
        response.Content.Headers.ContentLength = partial ? 65536 : bytes.Length;
        if (partial) response.Content.Headers.ContentRange = new(0, 65535, bytes.Length);
        response.Headers.ETag = new("\"original\"");
        var calls = 0;
        Task<HttpResponseMessage> Open(long offset, long? end, CancellationToken token)
        {
            calls++;
            Assert.Equal(0, offset);
            Assert.Null(end);
            var opened = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            opened.Content.Headers.ContentRange = new(0, bytes.Length - 1, bytes.Length);
            opened.Headers.ETag = response.Headers.ETag;
            return Task.FromResult(opened);
        }
        try
        {
            var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
                new("audio/mp4", "mp4", "alac"), 24, default, Open);
            Assert.True(result.IsSuccess, result.Error?.SafeMessage);
            Assert.Equal("alac", result.RequireValue().Media.Codec);
            Assert.InRange(result.RequireValue().DurationSeconds!.Value, 23.99, 24.01);
            Assert.Equal(partial ? 1 : 0, calls);
            Assert.False(input.Release.Task.IsCompleted);
            input.Release.TrySetResult();
            Assert.Equal(partial ? bytes[..65536] : bytes, await response.Content.ReadAsByteArrayAsync());
            if (partial) Assert.Equal(new ContentRangeHeaderValue(0, 65535, bytes.Length), response.Content.Headers.ContentRange);
        }
        finally { input.Release.TrySetResult(); }
    }

    [MediaToolsFact]
    public async Task ExpandedMoviePrefixRejectsAChangedRepresentation()
    {
        var bytes = await MovieAsync();
        using var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[..65536]) };
        response.Content.Headers.ContentRange = new(0, 65535, bytes.Length);
        response.Headers.ETag = new("\"original\"");
        Task<HttpResponseMessage> Open(long offset, long? end, CancellationToken token)
        {
            var opened = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
            opened.Content.Headers.ContentRange = new(0, bytes.Length - 1, bytes.Length);
            opened.Headers.ETag = new("\"changed\"");
            return Task.FromResult(opened);
        }
        var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
            new("audio/mp4", "mp4", "alac"), 24, default, Open);
        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
    }

    private static async Task<byte[]> MovieAsync()
    {
        var directory = Directory.CreateTempSubdirectory("allstarr-artwork-movie-").FullName;
        try
        {
            var wave = PlaybackAudioFixtures.Wave(24);
            RandomNumberGenerator.Fill(wave.AsSpan(44));
            await File.WriteAllBytesAsync(Path.Combine(directory, "input.wav"), wave);
            await File.WriteAllBytesAsync(Path.Combine(directory, "cover.rgb"), RandomNumberGenerator.GetBytes(512 * 512 * 3));
            await RunAsync(directory, "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "512x512", "-i", "cover.rgb", "-frames:v", "1", "-threads", "1", "cover.png");
            await RunAsync(directory, "-i", "input.wav", "-i", "cover.png", "-map", "0:a:0", "-map", "1:v:0", "-c:a", "alac", "-c:v", "copy", "-disposition:v:0", "attached_pic", "-movflags", "+faststart", "-threads", "1", "original.m4a");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "original.m4a"));
            Assert.True(bytes.Length > 2 * 1024 * 1024);
            var movieOffset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
            Assert.True(bytes.AsSpan(movieOffset + 4, 4).SequenceEqual("moov"u8));
            Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(movieOffset)) > 262144);
            return bytes;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task RunAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("ffmpeg") { WorkingDirectory = directory, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        await process.WaitForExitAsync();
        await errors;
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class GatedStream(byte[] bytes) : Stream
    {
        private int position;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position >= 2 * 1024 * 1024) await Release.Task.WaitAsync(token);
            var count = Math.Min(buffer.Length, bytes.Length - position);
            if (!Release.Task.IsCompleted) count = Math.Min(count, 2 * 1024 * 1024 - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count; return count;
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
