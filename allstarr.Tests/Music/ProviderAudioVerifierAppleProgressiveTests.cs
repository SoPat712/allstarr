using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Music;

namespace allstarr.Tests;

public sealed class ProviderAudioVerifierAppleProgressiveTests
{
    public static IEnumerable<object[]> Samples => AppleProgressiveFixtures.Samples;

    [MediaToolsTheory]
    [MemberData(nameof(Samples))]
    public async Task PrefixAcceptsGatewayOutputBeforeCompletionWithMeasuredFormatAndCatalogDuration(string file)
    {
        var sample = AppleProgressiveFixtures.Sample(file);
        var bytes = AppleProgressiveFixtures.Bytes(file);
        using var source = new AppleProgressiveFixtures.PendingTail(bytes);
        using var response = AppleProgressiveFixtures.Response(source, sample.Mime);
        try
        {
            var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
                sample.Media, sample.DurationSeconds, default);

            Assert.True(result.IsSuccess, result.Error?.Code);
            var facts = result.RequireValue();
            Assert.Equal(sample.Codec, facts.Media.Codec);
            Assert.Equal(sample.SampleRate, facts.Media.SampleRate);
            Assert.Equal(sample.BitDepth, facts.Media.BitDepth);
            Assert.Equal(sample.Channels, facts.Media.Channels);
            Assert.Equal(sample.DurationSeconds, facts.DurationSeconds);
            Assert.Null(facts.Size);
            Assert.Null(response.Content.Headers.ContentLength);
            Assert.False(source.Release.Task.IsCompleted);
            source.Release.TrySetResult();
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        }
        finally { source.Release.TrySetResult(); }
    }

    [MediaToolsTheory]
    [InlineData("alac-artwork.flac", "3", null, 3d)]
    [InlineData("alac-artwork.flac", null, 3d, 3d)]
    [InlineData("aac-48000.m4a", "3", null, 3d)]
    [InlineData("aac-48000.m4a", null, null, null)]
    [InlineData("alac-24-48000-original.m4a", null, 3d, 3d)]
    [InlineData("alac-16-44100.flac", "NaN", 3d, 3d)]
    [InlineData("alac-16-44100.flac", "Infinity", 3d, 3d)]
    [InlineData("alac-16-44100.flac", "0", 3d, 3d)]
    public async Task PrefixUsesValidDurationGuidanceWithoutTreatingFragmentDurationAsTheSongDuration(
        string file, string? guidance, double? catalog, double? duration)
    {
        var sample = AppleProgressiveFixtures.Sample(file);
        using var source = new AppleProgressiveFixtures.PendingTail(AppleProgressiveFixtures.Bytes(file));
        using var response = AppleProgressiveFixtures.Response(source, sample.Mime, guidance);
        try
        {
            var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response, sample.Media, catalog, default);
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.Equal(duration, result.RequireValue().DurationSeconds);
        }
        finally { source.Release.TrySetResult(); }
    }

    [MediaToolsTheory]
    [InlineData("truncated")]
    [InlineData("corrupt")]
    [InlineData("wrong-format")]
    [InlineData("wrong-duration")]
    public async Task PrefixRejectsInvalidGatewayAudioEvenWithDurationGuidance(string failure)
    {
        var sample = AppleProgressiveFixtures.Sample("alac-16-44100.flac");
        var bytes = AppleProgressiveFixtures.Bytes(sample.File);
        if (failure == "truncated") bytes = bytes[..AppleProgressiveFixtures.PrefixLength(bytes)];
        if (failure == "corrupt") bytes.AsSpan(AppleProgressiveFixtures.FlacAudioOffset(bytes)).Fill(0);
        using var source = new AppleProgressiveFixtures.PendingTail(bytes);
        source.Release.TrySetResult();
        using var response = AppleProgressiveFixtures.Response(source, sample.Mime,
            failure == "wrong-duration" ? "30" : "3");

        var result = await new ProviderAudioVerifier().VerifyPrefixAsync(response,
            failure == "wrong-format" ? new("audio/mp4", "mp4", "alac") : sample.Media, 3, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.IncompatibleMedia, result.Error!.Kind);
    }
}

internal static class AppleProgressiveFixtures
{
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AppleProgressive");
    private static readonly SampleFacts[] Manifest = JsonSerializer.Deserialize<SampleFacts[]>(
        File.ReadAllText(Path.Combine(DirectoryPath, "manifest.json")),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
    internal static IEnumerable<object[]> Samples => Manifest.Select(sample => new object[] { sample.File });
    internal static SampleFacts Sample(string file) => Manifest.Single(sample => sample.File == file);
    internal static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(DirectoryPath, file));

    internal sealed record SampleFacts(string File, string Mime, string Codec, int? BitDepth,
        int SampleRate, int Channels, double DurationSeconds)
    {
        internal ProviderMediaFormat Media => new(Mime, Codec == "flac" ? "flac" : "mp4", Codec);
    }

    internal static HttpResponseMessage Response(Stream source, string mime, string? duration = "3")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
        response.Content.Headers.ContentType = new(mime);
        response.Headers.TransferEncodingChunked = true;
        if (duration != null) response.Headers.TryAddWithoutValidation("X-Allstarr-Duration-Seconds", duration);
        return response;
    }

    internal static int FlacAudioOffset(byte[] bytes)
    {
        var offset = 4;
        while (offset < bytes.Length)
        {
            var last = (bytes[offset] & 128) != 0;
            offset += 4 + (bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);
            if (last) return offset;
        }
        throw new InvalidDataException();
    }

    internal static int PrefixLength(byte[] bytes)
    {
        if (bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8))
        {
            var offset = FlacAudioOffset(bytes);
            return offset + (bytes.Length - offset) / 2;
        }
        var position = 0;
        var fragments = 0;
        var fallback = 0;
        while (position < bytes.Length - 8)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            if (size < 8) throw new InvalidDataException();
            var media = bytes.AsSpan(position + 4, 4).SequenceEqual("mdat"u8);
            if (media) fallback = position + 8 + (size - 8) / 2;
            position += size;
            if (media && ++fragments == 2) return position;
        }
        return fallback > 0 ? fallback : throw new InvalidDataException();
    }

    internal sealed class PendingTail(byte[] bytes) : Stream
    {
        private readonly int prefixLength = PrefixLength(bytes);
        private int position;
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (position >= prefixLength) await Release.Task.WaitAsync(token);
            var end = Release.Task.IsCompleted ? bytes.Length : prefixLength;
            var count = Math.Min(buffer.Length, end - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
