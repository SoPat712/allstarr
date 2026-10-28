using System.Buffers.Binary;
using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class AudioMediaFactsTests
{
    [Fact]
    public void ProbeDoesNotTreatUnknownRatingsAsClean()
    {
        var facts = AudioMediaFacts.ParseProbe("""
            {"streams":[{"codec_name":"flac","sample_rate":"44100","channels":2}],
             "format":{"format_name":"flac","duration":"200","tags":{"title":"Song","explicit":"2","itunesadvisory":"0"}}}
            """, 1000000);
        Assert.Null(facts!.Tags!.IsExplicit);
    }

    [Fact]
    public async Task CachedFileTagsRemainAvailableWithoutProbing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audio-tags-{Guid.NewGuid():N}.wav");
        try
        {
            var bytes = new byte[1024];
            Wave()[..44].CopyTo(bytes, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)(bytes.Length - 44));
            await File.WriteAllBytesAsync(path, bytes);
            using (var file = TagLib.File.Create(path))
            {
                file.Tag.Title = "Received song";
                file.Tag.Performers = ["Received artist"];
                file.Tag.Album = "Received album";
                file.Tag.ISRC = "USABC1234567";
                file.Save();
            }
            var original = await File.ReadAllBytesAsync(path);
            var tags = AudioMediaFacts.ReadTags(path, new("audio/wav", "wav", "pcm_s24le"));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal("Received song", tags!.Title);
            Assert.Equal("Received artist", tags.Artist);
            Assert.Equal("Received album", tags.Album);
            Assert.Equal("USABC1234567", tags.Isrc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProbeRetainsOnlyKnownAudioTags()
    {
        var facts = AudioMediaFacts.ParseProbe("""
            {"streams":[{"codec_name":"flac","sample_rate":"44100","channels":2}],
             "format":{"format_name":"flac","duration":"200","tags":{"TITLE":"Received title","artist":"Received artist",
               "album":"Received album","ISRC":"USABC1234567","explicit":"0","comment":"private-value"}}}
            """, 1000000);
        Assert.Equal("Received title", facts!.Tags!.Title);
        Assert.Equal("Received artist", facts.Tags.Artist);
        Assert.Equal("USABC1234567", facts.Tags.Isrc);
        Assert.False(facts.Tags.IsExplicit);
        Assert.DoesNotContain("private-value", System.Text.Json.JsonSerializer.Serialize(facts));
    }

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
