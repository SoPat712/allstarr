using System.Buffers.Binary;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class AudioMediaFactsTests
{
    [Fact]
    public void SingleMpegFrameDoesNotClaimAWholeTrackBitrate()
    {
        var facts = AudioMediaFacts.ReadPrefix([0xff, 0xfb, 0x90, 0x00], 5000000);
        Assert.NotNull(facts);
        Assert.Equal("mp3", facts.Media.Codec);
        Assert.Equal(44100, facts.Media.SampleRate);
        Assert.Null(facts.Media.Bitrate);
    }

    [Fact]
    public void ProbeKeepsFiniteGainIncludingZeroAndRejectsNegativePeaks()
    {
        var facts = AudioMediaFacts.ParseProbe("""
            {"streams":[{"codec_name":"alac","sample_rate":"96000","channels":2,"bits_per_raw_sample":"24"}],
             "format":{"format_name":"mov,mp4,m4a,3gp,3g2,mj2","duration":"115.2",
               "tags":{"replaygain_track_gain":"0 dB","replaygain_album_gain":"-6.5 dB","replaygain_track_peak":"0.98","replaygain_album_peak":"-1"}}}
            """, 12000000);
        Assert.NotNull(facts);
        Assert.Equal("mp4", facts.Media.Container);
        Assert.Equal("alac", facts.Media.Codec);
        Assert.Equal(0d, facts.ReplayGain!.TrackGain);
        Assert.Equal(-6.5, facts.ReplayGain.AlbumGain);
        Assert.Null(facts.ReplayGain.AlbumPeak);
    }

    [Fact]
    public void WaveHeaderOverridesADeclaredFlacWithExactPcmFacts()
    {
        var bytes = Wave();
        var facts = AudioMediaFacts.ReadPrefix(bytes, 30645098);
        Assert.NotNull(facts);
        Assert.Equal("wav", facts.Media.Container);
        Assert.Equal("pcm_s24le", facts.Media.Codec);
        Assert.Equal("audio/wav", facts.Media.MimeType);
        Assert.Equal(44100, facts.Media.SampleRate);
        Assert.Equal(24, facts.Media.BitDepth);
        Assert.Equal(2, facts.Media.Channels);
        Assert.Equal(2116800, facts.Media.Bitrate);
        Assert.Equal(30645098, facts.Size);
        Assert.Equal(115.2, facts.DurationSeconds!.Value, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(35)]
    public void TruncatedHeadersDoNotInventFacts(int length) =>
        Assert.Null(AudioMediaFacts.ReadPrefix(Wave()[..length], null));

    internal static byte[] Wave()
    {
        var bytes = new byte[80];
        "RIFF"u8.CopyTo(bytes);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 264600);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 24);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 30481920);
        return bytes;
    }
}
