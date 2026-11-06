using System.Globalization;
using allstarr.Core.Enrichment;

namespace allstarr.Tests.Storage;

public sealed class PromotionReplayGainTests
{
    [Fact]
    public void Summary_UsesFinalIntegratedLoudnessAndTruePeak()
    {
        const string output = """
            t: 0.4 I: -70.0 LUFS FTPK: -3.0 -4.0 dBFS
            Summary:
              Integrated loudness:
                I: -70.0 LUFS
              True peak:
                Peak: -inf dBFS
            Summary:
              Integrated loudness:
                I: -10.5 LUFS
              Loudness range:
                LRA: 3.0 LU
              True peak:
                Peak: -1.0 dBFS
            """;

        var result = PromotionReplayGain.ParseSummary(output);

        Assert.Equal(-7.5, result.GainDb);
        Assert.Equal(Math.Pow(10, -1.0 / 20), result.Peak, 8);
        Assert.Equal("-7.50 dB", PromotionReplayGain.FormatGain(result.GainDb));
        Assert.Equal("0.89125094", PromotionReplayGain.FormatPeak(result.Peak));
    }

    [Theory]
    [InlineData(-23.0, 5.0)]
    [InlineData(-18.0, 0.0)]
    [InlineData(-6.0, -12.0)]
    public void Gain_TargetsMinusEighteenLufs(double loudness, double expected)
    {
        var result = PromotionReplayGain.ParseSummary(FormattableString.Invariant($"""
            Integrated loudness:
                I: {loudness} LUFS
            True peak:
                Peak: 0.0 dBFS
            """));

        Assert.Equal(expected, result.GainDb);
        Assert.Equal(1, result.Peak);
    }

    [Fact]
    public void Silence_HasZeroGainAndPeak()
    {
        var result = PromotionReplayGain.ParseSummary("Integrated loudness:\nI: -70.0 LUFS\nTrue peak:\nPeak: -inf dBFS");

        Assert.Equal(0, result.GainDb);
        Assert.Equal(0, result.Peak);
    }

    [Fact]
    public void AudioBelowTheAbsoluteGate_DoesNotReceiveExtremePositiveGain()
    {
        var result = PromotionReplayGain.ParseSummary("Integrated loudness:\nI: -70.0 LUFS\nTrue peak:\nPeak: -80.0 dBFS");

        Assert.Equal(0, result.GainDb);
        Assert.Equal(0.0001, result.Peak, 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I: -12.0 LUFS\nPeak: -1.0 dBFS")]
    [InlineData("Integrated loudness:\nI: NaN LUFS\nTrue peak:\nPeak: 0 dBFS")]
    [InlineData("Integrated loudness:\nI: -12 LUFS\nSample peak:\nPeak: -1 dBFS")]
    public void Summary_RejectsMissingOrInvalidMeasurements(string output)
    {
        Assert.Throws<InvalidDataException>(() => PromotionReplayGain.ParseSummary(output));
    }

    [Fact]
    public void Formats_AreIndependentOfCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("+4.25 dB", PromotionReplayGain.FormatGain(4.25));
            Assert.Equal("0.50000000", PromotionReplayGain.FormatPeak(0.5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void AlbumFilter_ConcatenatesCompletePcmStreamsBeforeMeasuring()
    {
        var filter = PromotionReplayGain.BuildAlbumFilter(3);

        Assert.Contains("[0:a:0]", filter);
        Assert.Contains("[1:a:0]", filter);
        Assert.Contains("[2:a:0]", filter);
        Assert.Contains("concat=n=3:v=0:a=1", filter);
        Assert.EndsWith("ebur128=peak=true:framelog=verbose[analysis]", filter);
        Assert.DoesNotContain("loudnorm", filter);
        Assert.DoesNotContain("volume", filter);
    }

    [Fact]
    public void AlbumChannels_PreserveMonoAndSurroundAtUnity()
    {
        Assert.Equal("FC=c0", PromotionReplayGain.ChannelMap("mono", 1));
        Assert.Equal("FL=c0|FR=c1", PromotionReplayGain.ChannelMap(null, 2));
        Assert.Equal("FL=c0|FR=c1|FC=c2|LFE=c3|SL=c4|SR=c5", PromotionReplayGain.ChannelMap("5.1(side)", 6));
        Assert.Throws<InvalidDataException>(() => PromotionReplayGain.ChannelMap(null, 6));
        Assert.Throws<InvalidDataException>(() => PromotionReplayGain.ChannelMap("6.1", 7));
    }

    [Fact]
    public async Task Analyze_RejectsEmptySetsAndObservesCancellationBeforeStarting()
    {
        var analyzer = new PromotionReplayGain();
        await Assert.ThrowsAsync<ArgumentException>(() => analyzer.AnalyzeAsync([], CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(["track.flac"], cancellation.Token));
    }

    [Fact]
    public async Task Analyze_MeasuresTrackAndAlbumAudioWithTruePeak()
    {
        var directory = Path.Combine(Path.GetTempPath(), "promotion-gain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var quiet = Path.Combine(directory, "quiet.wav");
            var loud = Path.Combine(directory, "loud.wav");
            WriteTone(quiet, 0.1);
            WriteTone(loud, 0.5);
            var result = await new PromotionReplayGain().AnalyzeAsync([quiet, loud], default);
            Assert.Equal(2, result.Tracks.Count);
            Assert.InRange(result.Tracks[0].GainDb - result.Tracks[1].GainDb, 13.7, 14.3);
            Assert.InRange(result.AlbumGainDb, result.Tracks[1].GainDb - 0.2, result.Tracks[0].GainDb + 0.2);
            Assert.Equal(result.Tracks.Max(track => track.Peak), result.AlbumPeak);
            Assert.InRange(result.AlbumPeak, 0.49, 0.51);
            Assert.True(Math.Abs(result.AlbumGainDb - result.Tracks.Average(track => track.GainDb)) > 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void WriteTone(string path, double amplitude)
    {
        using var wave = new BinaryWriter(File.Create(path));
        const int samples = 44100 * 3;
        wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + samples * 2);
        wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16); wave.Write((short)1);
        wave.Write((short)1); wave.Write(44100); wave.Write(88200); wave.Write((short)2); wave.Write((short)16);
        wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(samples * 2);
        for (var index = 0; index < samples; index++) wave.Write((short)(short.MaxValue * amplitude * Math.Sin(2 * Math.PI * 1000 * index / 44100)));
    }

    [Fact]
    public void XiphTags_PersistReplayGainLyricsDatesAndNumbering()
    {
        var tag = new TagLib.Ogg.XiphComment();
        ApplyMetadata(tag);

        var saved = new TagLib.Ogg.XiphComment(tag.Render(false));
        AssertMetadata(saved);
        Assert.Equal("2020-02-03", Assert.Single(saved.GetField("DATE")));
        Assert.Equal("1997-04-05", Assert.Single(saved.GetField("ORIGINALDATE")));
        Assert.Equal("[00:01.250]First line\n[00:03.000]Second line", Assert.Single(saved.GetField("LYRICS")));
    }

    [Fact]
    public void Id3Tags_IncludeSynchronizedLyricsAndFullDates()
    {
        var tag = new TagLib.Id3v2.Tag { Version = 4 };
        ApplyMetadata(tag);

        AssertMetadata(tag);
        Assert.Equal("2020-02-03", Assert.Single(TagLib.Id3v2.TextInformationFrame.Get(tag, "TDRC", false).Text));
        Assert.Equal("1997-04-05", Assert.Single(TagLib.Id3v2.TextInformationFrame.Get(tag, "TDOR", false).Text));
        var synced = TagLib.Id3v2.SynchronisedLyricsFrame.Get(tag, "", "eng", TagLib.Id3v2.SynchedTextType.Lyrics, false);
        Assert.NotNull(synced);
        Assert.Equal(TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds, synced.Format);
        Assert.Collection(synced.Text,
            first => { Assert.Equal(1250, first.Time); Assert.Equal("First line", first.Text); },
            second => { Assert.Equal(3000, second.Time); Assert.Equal("Second line", second.Text); });
    }

    [Theory]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void Id3Tags_RetainReplayGainAndLyricsAfterSerialization(byte version)
    {
        var tag = new TagLib.Id3v2.Tag { Version = version };
        ApplyMetadata(tag);

        var saved = new TagLib.Id3v2.Tag(tag.Render());

        AssertMetadata(saved);
        Assert.Equal("2020-02-03", Assert.Single(TagLib.Id3v2.UserTextInformationFrame.Get(saved, "DATE", false).Text));
        Assert.Equal("1997-04-05", Assert.Single(TagLib.Id3v2.UserTextInformationFrame.Get(saved, "ORIGINALDATE", false).Text));
        Assert.Equal(2, TagLib.Id3v2.SynchronisedLyricsFrame.Get(saved, "", "eng", TagLib.Id3v2.SynchedTextType.Lyrics, false).Text.Length);
    }

    [Fact]
    public void SynchronizedLyrics_ApplyOffsetsAndRepeatedTimestamps()
    {
        var parsed = TagLibManagedTagFileMutator.ParseSynchronizedLyrics("[offset:-250]\n[00:01.5][00:03.00]Repeated\nPlain text");

        Assert.Collection(parsed,
            first => { Assert.Equal(1750, first.Time); Assert.Equal("Repeated", first.Text); },
            second => { Assert.Equal(3250, second.Time); Assert.Equal("Repeated", second.Text); });
    }

    [Fact]
    public void PlainLyrics_RemoveAnEarlierSynchronizedFrame()
    {
        var tag = new TagLib.Id3v2.Tag();
        ApplyMetadata(tag);

        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string> { ["lyrics"] = "Plain lyrics" });

        Assert.Equal("Plain lyrics", tag.Lyrics);
        Assert.Null(TagLib.Id3v2.SynchronisedLyricsFrame.Get(tag, "", "eng", TagLib.Id3v2.SynchedTextType.Lyrics, false));
    }

    [Fact]
    public void ReplayGain_RejectsInvalidNumbersAndPreservesArtwork()
    {
        var tag = new TagLib.Ogg.XiphComment();
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII=");
        var picture = new TagLib.Picture(new TagLib.ByteVector(image));
        tag.Pictures = [picture];

        TagLibManagedTagFileMutator.ApplyTags(tag, new Dictionary<string, string> { ["REPLAYGAIN_TRACK_GAIN"] = "+2.00 dB" });

        Assert.Equal(image, Assert.Single(tag.Pictures).Data.Data);
        Assert.Throws<InvalidDataException>(() => TagLibManagedTagFileMutator.ApplyTags(tag,
            new Dictionary<string, string> { ["REPLAYGAIN_TRACK_PEAK"] = "NaN" }));
    }

    [Fact]
    public void Mp4Tags_UseReplayGainAndLyricsFields()
    {
        var tag = new TagLib.Mpeg4.AppleTag(new TagLib.Mpeg4.IsoUserDataBox());
        ApplyMetadata(tag);

        AssertMetadata(tag);
        Assert.Equal("2020-02-03", tag.GetText(new TagLib.ByteVector(new byte[] { 0xa9, 0x64, 0x61, 0x79 })).Single());
        Assert.Equal("1997-04-05", tag.GetDashBox("com.apple.iTunes", "ORIGINALDATE"));
    }

    private static void ApplyMetadata(TagLib.Tag tag) => TagLibManagedTagFileMutator.ApplyTags(tag,
        new Dictionary<string, string>
        {
            ["REPLAYGAIN_TRACK_GAIN"] = "-7.50 dB",
            ["REPLAYGAIN_TRACK_PEAK"] = "0.89125094",
            ["REPLAYGAIN_ALBUM_GAIN"] = "-6.25 dB",
            ["REPLAYGAIN_ALBUM_PEAK"] = "1.05000000",
            ["disc"] = "2",
            ["totaldiscs"] = "3",
            ["track"] = "4",
            ["totaltracks"] = "12",
            ["date"] = "2020-02-03",
            ["originaldate"] = "1997-04-05",
            ["comment"] = "Lossy source",
            ["lyrics"] = "[00:01.250]First line\n[00:03.000]Second line"
        });

    private static void AssertMetadata(TagLib.Tag tag)
    {
        Assert.Equal(-7.5, tag.ReplayGainTrackGain);
        Assert.Equal(0.89125094, tag.ReplayGainTrackPeak, 8);
        Assert.Equal(-6.25, tag.ReplayGainAlbumGain);
        Assert.Equal(1.05, tag.ReplayGainAlbumPeak, 8);
        Assert.Equal(2U, tag.Disc);
        Assert.Equal(3U, tag.DiscCount);
        Assert.Equal(4U, tag.Track);
        Assert.Equal(12U, tag.TrackCount);
        Assert.Equal("Lossy source", tag.Comment);
        Assert.Equal("[00:01.250]First line\n[00:03.000]Second line", tag.Lyrics);
    }
}
