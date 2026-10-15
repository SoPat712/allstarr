using System.Buffers.Binary;
using System.Text;
using allstarr.Services.Common;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Capabilities;
using System.Net;

namespace allstarr.Tests;

public sealed class MediaDurationHeadersTests
{
    [Fact]
    public void Flac_ZeroTotalSamplesIsPatched_AndMetadataTailIsPreserved()
    {
        var bytes = CreateFlac(sampleRate: 44_100, totalSamples: 0);
        var before = bytes.ToArray();
        var beforePacked = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, 8));

        Assert.True(MediaDurationHeaders.TryPatch(bytes.AsSpan(), "flac", TimeSpan.FromSeconds(2.5)));

        var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(18, 8));
        var totalMask = (1UL << 36) - 1;
        Assert.Equal(110_250UL, packed & totalMask);
        Assert.Equal(beforePacked & ~totalMask, packed & ~totalMask);
        Assert.Equal(before.AsSpan(26, 16).ToArray(), bytes.AsSpan(26, 16).ToArray());
        Assert.Equal(before.AsSpan(42).ToArray(), bytes.AsSpan(42).ToArray());

        Assert.False(MediaDurationHeaders.TryPatch(bytes.AsSpan(), "flac", TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public void Flac_InvalidOrUnrepresentableInputsAreLeftUnchanged()
    {
        var tooLarge = TimeSpan.FromSeconds((double)((1UL << 36) / 44_100UL) + 1);
        var wrongType = CreateFlac();
        wrongType[4] = 1;
        var wrongLength = CreateFlac();
        wrongLength[7] = 33;
        var cases = new[]
        {
            (CreateFlac(totalSamples: 1), TimeSpan.FromSeconds(1)),
            (CreateFlac(sampleRate: 0), TimeSpan.FromSeconds(1)),
            (CreateFlac()[..41], TimeSpan.FromSeconds(1)),
            (wrongType, TimeSpan.FromSeconds(1)),
            (wrongLength, TimeSpan.FromSeconds(1)),
            (CreateFlac(), tooLarge),
            (CreateFlac(), TimeSpan.Zero),
        };

        foreach (var (bytes, duration) in cases)
            AssertUnchanged(bytes, "flac", duration);
        AssertUnchanged(CreateFlac(), "ogg", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Id3_V24FooterAndReservedFlagsAreValidated()
    {
        var header = new byte[10];
        "ID3"u8.CopyTo(header); header[3] = 4; header[5] = 0x10; header[9] = 4;
        Assert.True(MediaDurationHeaders.TryGetId3TagLength(header, out var length));
        Assert.Equal(24, length);
        header[5] = 0x08; Assert.False(MediaDurationHeaders.TryGetId3TagLength(header, out _));
        header[3] = 2; header[5] = 0x40; Assert.False(MediaDurationHeaders.TryGetId3TagLength(header, out _));
        header[3] = 4; header[5] = 0; header[6] = 1; Assert.False(MediaDurationHeaders.TryGetId3TagLength(header, out _));
    }

    [Fact]
    public void Mp3_XingFrameCountIsPatched_AndTocAndAudioArePreserved()
    {
        var fixture = CreateXing(tagName: "Xing");
        var beforeByteCount = fixture.Bytes.AsSpan(fixture.TagStart + 12, 4).ToArray();
        var beforeToc = fixture.Bytes.AsSpan(fixture.TagStart + 16, 100).ToArray();
        var beforeAudio = fixture.Bytes.AsSpan(fixture.TagStart + 120, fixture.FrameLength - (fixture.TagStart - fixture.FrameStart) - 120).ToArray();

        Assert.True(MediaDurationHeaders.TryPatch(fixture.Bytes.AsSpan(), "mp3", TimeSpan.FromSeconds(1)));

        Assert.Equal(38U, BinaryPrimitives.ReadUInt32BigEndian(fixture.Bytes.AsSpan(fixture.CountOffset, 4)));
        Assert.Equal(beforeByteCount, fixture.Bytes.AsSpan(fixture.TagStart + 12, 4).ToArray());
        Assert.Equal(beforeToc, fixture.Bytes.AsSpan(fixture.TagStart + 16, 100).ToArray());
        Assert.Equal(beforeAudio, fixture.Bytes.AsSpan(fixture.TagStart + 120, beforeAudio.Length).ToArray());
        Assert.False(MediaDurationHeaders.TryPatch(fixture.Bytes.AsSpan(), "mp3", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Mp3_InfoAndVbriZeroCountsArePatched()
    {
        var info = CreateXing(tagName: "Info");
        Assert.True(MediaDurationHeaders.TryPatch(info.Bytes.AsSpan(), "mp3", TimeSpan.FromSeconds(1)));
        Assert.Equal(38U, BinaryPrimitives.ReadUInt32BigEndian(info.Bytes.AsSpan(info.CountOffset, 4)));

        var footer = CreateXing(tagName: "Xing", footer: true);
        Assert.True(MediaDurationHeaders.TryPatch(footer.Bytes.AsSpan(), "mp3", TimeSpan.FromSeconds(1)));
        Assert.Equal(38U, BinaryPrimitives.ReadUInt32BigEndian(footer.Bytes.AsSpan(footer.CountOffset, 4)));

        var vbri = CreateVbri();
        Assert.True(MediaDurationHeaders.TryPatch(vbri.Bytes.AsSpan(), "mp3", TimeSpan.FromSeconds(1)));
        Assert.Equal(38U, BinaryPrimitives.ReadUInt32BigEndian(vbri.Bytes.AsSpan(vbri.CountOffset, 4)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(vbri.Bytes.AsSpan(vbri.TagStart + 4, 2)));
    }

    [Fact]
    public void Mp3_TruncatedCrcExistingAndLameExtendedFramesAreLeftUnchanged()
    {
        var valid = CreateXing(tagName: "Xing");
        var truncated = valid.Bytes[..^1];
        var crc = CreateXing(tagName: "Xing", crcProtected: true);
        var existing = CreateXing(tagName: "Xing", count: 7);
        var lame = CreateXing(tagName: "Info");
        "LAME"u8.CopyTo(lame.Bytes.AsSpan(lame.TagStart + 120, 4));
        var tinyVbri = CreateVbri();
        BinaryPrimitives.WriteUInt32BigEndian(tinyVbri.Bytes.AsSpan(tinyVbri.TagStart + 10, 4), 1);
        foreach (var bytes in new[] { truncated, crc.Bytes, existing.Bytes, lame.Bytes, tinyVbri.Bytes })
            AssertUnchanged(bytes, "mp3", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Mp4_ZeroDurationsArePatched_AndSampleTableIsPreserved()
    {
        var fixture = CreateMovie(movieTimescale: 1_000, mediaTimescale: 48_000, sampleCount: 144_000);
        var before = fixture.Bytes.ToArray();
        Assert.True(MediaDurationHeaders.TryPatch(fixture.Bytes.AsSpan(), "mp4", TimeSpan.FromSeconds(3)));
        Assert.Equal(3_000U, BinaryPrimitives.ReadUInt32BigEndian(fixture.Bytes.AsSpan(fixture.MvhdDurationOffset, 4)));
        Assert.Equal(144_000U, BinaryPrimitives.ReadUInt32BigEndian(fixture.Bytes.AsSpan(fixture.MdhdDurationOffset, 4)));
        Assert.Equal(3_000U, BinaryPrimitives.ReadUInt32BigEndian(fixture.Bytes.AsSpan(fixture.TkhdDurationOffset, 4)));
        Assert.Equal(before.AsSpan(fixture.SttsOffset, fixture.SttsLength).ToArray(), fixture.Bytes.AsSpan(fixture.SttsOffset, fixture.SttsLength).ToArray());
    }

    [Fact]
    public void Mp4_ExtendedVersionOneBoxesArePatchedWith64BitDurations()
    {
        var fixture = CreateMovie(
            movieTimescale: 1_000,
            mediaTimescale: 24_000,
            sampleCount: 48_000,
            versionOne: true,
            extendedMvhd: true,
            extendedMdhd: true,
            extendedTkhd: true);

        Assert.True(MediaDurationHeaders.TryPatch(fixture.Bytes.AsSpan(), "mp4", TimeSpan.FromSeconds(2)));

        Assert.Equal(2_000UL, BinaryPrimitives.ReadUInt64BigEndian(fixture.Bytes.AsSpan(fixture.MvhdDurationOffset, 8)));
        Assert.Equal(48_000UL, BinaryPrimitives.ReadUInt64BigEndian(fixture.Bytes.AsSpan(fixture.MdhdDurationOffset, 8)));
        Assert.Equal(2_000UL, BinaryPrimitives.ReadUInt64BigEndian(fixture.Bytes.AsSpan(fixture.TkhdDurationOffset, 8)));
    }

    [Fact]
    public void Mp4_MalformedMismatchedMultipleAndPreexistingDurationsAreLeftUnchanged()
    {
        AssertUnchanged(CreateMovie(movieTimescale: 1_000, mediaTimescale: 48_000, sampleCount: 48_000).Bytes, "mp4", TimeSpan.FromSeconds(3));
        AssertUnchanged(CreateMovie(movieTimescale: 1_000, mediaTimescale: 48_000, sampleCount: 144_000, trackCount: 2).Bytes, "mp4", TimeSpan.FromSeconds(3));
        AssertUnchanged(CreateMovie(movieTimescale: 1_000, mediaTimescale: 48_000, sampleCount: 144_000, movieDuration: 3_000, mediaDuration: 144_000, trackDuration: 3_000).Bytes, "mp4", TimeSpan.FromSeconds(3));

        var complete = CreateMovie(movieTimescale: 1_000, mediaTimescale: 48_000, sampleCount: 144_000);
        AssertUnchanged(complete.Bytes[..(complete.SttsOffset + complete.SttsLength - 1)], "mp4", TimeSpan.FromSeconds(3));
        Assert.True(MediaDurationHeaders.TryPatch(complete.Bytes[..(FindBox(complete.Bytes, "mdat") + 8)], "mp4", TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Mp4_IncompleteFixedHeaderFieldsRemainUnchanged()
    {
        var fixture = CreateMovie(1000, 48000, 144000);
        var start = FindBox(fixture.Bytes, "mvhd");
        var trimmed = fixture.Bytes[..(start + 32)].Concat(fixture.Bytes[(start + 108)..]).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(trimmed.AsSpan(start, 4), 32);
        BinaryPrimitives.WriteUInt32BigEndian(trimmed.AsSpan(0, 4), BinaryPrimitives.ReadUInt32BigEndian(fixture.Bytes.AsSpan(0, 4)) - 76);
        AssertUnchanged(trimmed, "mp4", TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("mp4")]
    public async Task SharedTransportPatchesHeaderAndReplaysUnbufferedTail(string container)
    {
        var fixture = container == "mp3" ? CreateXing("Xing").Bytes : CreateMovie(1000, 48000, 144000).Bytes;
        var expected = fixture.ToArray();
        var duration = TimeSpan.FromSeconds(container == "mp3" ? 1 : 3);
        Assert.True(MediaDurationHeaders.TryPatch(expected, container, duration));
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(fixture)) };
        response.Headers.Add("X-Allstarr-Duration-Seconds", duration.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var routed = new ProtocolProviderStream(response, new("sequential", new Uri("https://media.example.test/track"),
            DateTimeOffset.UtcNow.AddMinutes(1), false, false, new("audio/" + container, container, container),
            ProviderStreamRetryBehavior.DoNotRetry), "fixture");
        StreamDurationGuidance.Attach(routed, () => throw new InvalidOperationException("Probe should supply duration"),
            new Microsoft.AspNetCore.Http.DefaultHttpContext().Response);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    private static void AssertUnchanged(byte[] bytes, string container, TimeSpan duration)
    {
        var before = bytes.ToArray();
        Assert.False(MediaDurationHeaders.TryPatch(bytes.AsSpan(), container, duration));
        Assert.Equal(before, bytes);
    }

    private static byte[] CreateFlac(uint sampleRate = 44_100, ulong totalSamples = 0)
    {
        var bytes = new byte[64];
        "fLaC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0x0000_0022);
        var packed = ((ulong)sampleRate << 44) | (1UL << 41) | (15UL << 36) | totalSamples;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
        for (var index = 26; index < 42; index++)
            bytes[index] = (byte)(index * 3);
        for (var index = 42; index < bytes.Length; index++)
            bytes[index] = (byte)(255 - index);
        return bytes;
    }

    private static Mp3Fixture CreateXing(string tagName, uint count = 0, bool crcProtected = false, bool footer = false)
    {
        var id3Length = footer ? 24 : 14;
        const int frameLength = 417;
        var bytes = CreateMp3Frame(id3Length, frameLength, crcProtected, footer);
        var frameStart = id3Length;
        var tagStart = frameStart + 4 + 32;
        tagNameu8(tagName).CopyTo(bytes.AsSpan(tagStart, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tagStart + 4, 4), 7);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tagStart + 8, 4), count);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tagStart + 12, 4), (uint)frameLength * 38U);
        for (var index = 0; index < 100; index++)
            bytes[tagStart + 16 + index] = (byte)(index + 1);
        return new Mp3Fixture(bytes, frameStart, tagStart, tagStart + 8, frameLength);
    }
    private static Mp3Fixture CreateVbri()
    {
        const int id3Length = 14;
        const int frameLength = 417;
        var bytes = CreateMp3Frame(id3Length, frameLength, crcProtected: false);
        var frameStart = id3Length;
        var tagStart = frameStart + 36;
        "VBRI"u8.CopyTo(bytes.AsSpan(tagStart, 4));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(tagStart + 4, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tagStart + 10, 4), (uint)frameLength * 38U); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(tagStart + 18, 2), 1); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(tagStart + 20, 2), 1); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(tagStart + 22, 2), 1); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(tagStart + 24, 2), 1); bytes[tagStart + 26] = 0x7f;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tagStart + 14, 4), 0);
        return new Mp3Fixture(bytes, frameStart, tagStart, tagStart + 14, frameLength);
    }
    private static byte[] CreateMp3Frame(int id3Length, int frameLength, bool crcProtected, bool footer = false)
    {
        var bytes = new byte[id3Length + frameLength];
        "ID3"u8.CopyTo(bytes);
        bytes[3] = 4;
        bytes[5] = footer ? (byte)0x10 : (byte)0;
        bytes[9] = 4;
        var frameStart = id3Length;
        bytes[frameStart] = 0xff;
        bytes[frameStart + 1] = crcProtected ? (byte)0xfa : (byte)0xfb;
        bytes[frameStart + 2] = 0x90;
        bytes[frameStart + 3] = 0;
        for (var index = frameStart + 4; index < bytes.Length; index++)
            bytes[index] = (byte)(index * 11);
        return bytes;
    }
    private static byte[] tagNameu8(string value) => Encoding.ASCII.GetBytes(value);
    private static Mp4Fixture CreateMovie(
        uint movieTimescale,
        uint mediaTimescale,
        uint sampleCount,
        uint movieDuration = 0,
        uint mediaDuration = 0,
        uint trackDuration = 0,
        int trackCount = 1,
        bool versionOne = false,
        bool extendedMvhd = false,
        bool extendedMdhd = false,
        bool extendedTkhd = false)
    {
        var mvhd = CreateMvhd(movieTimescale, movieDuration, versionOne, extendedMvhd);
        var tracks = new List<byte[]>();
        for (var index = 0; index < trackCount; index++)
            tracks.Add(CreateTrack(mediaTimescale, sampleCount, mediaDuration, trackDuration, versionOne, extendedMdhd, extendedTkhd, index + 1));
        var moovPayload = mvhd.Concat(tracks.SelectMany(item => item)).ToArray();
        var moov = Box("moov", moovPayload);
        var mdat = Box("mdat", [0x42, 0x24, 0x18, 0x06]);
        var bytes = moov.Concat(mdat).ToArray();
        var mvhdStart = FindBox(bytes, "mvhd");
        var mdhdStart = FindBox(bytes, "mdhd");
        var tkhdStart = FindBox(bytes, "tkhd");
        var mvhdDurationOffset = DurationOffset(bytes, mvhdStart, versionOne, isTrackHeader: false);
        var mdhdDurationOffset = DurationOffset(bytes, mdhdStart, versionOne, isTrackHeader: false);
        var tkhdDurationOffset = DurationOffset(bytes, tkhdStart, versionOne, isTrackHeader: true);
        var sttsStart = FindBox(bytes, "stts");
        var sttsLength = checked((int)ReadBoxSize(bytes, sttsStart));
        return new Mp4Fixture(bytes, mvhdDurationOffset, mdhdDurationOffset, tkhdDurationOffset, sttsStart, sttsLength);
    }
    private static byte[] CreateMvhd(uint timescale, uint duration, bool versionOne, bool extended)
    {
        var payload = new byte[versionOne ? 112 : 100];
        payload[0] = versionOne ? (byte)1 : (byte)0;
        var timescaleOffset = versionOne ? 20 : 12;
        var durationOffset = versionOne ? 24 : 16;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(timescaleOffset, 4), timescale);
        if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(durationOffset, 8), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(durationOffset, 4), duration);
        return Box("mvhd", payload, extended);
    }
    private static byte[] CreateTrack(uint timescale, uint sampleCount, uint mediaDuration, uint trackDuration, bool versionOne, bool extendedMdhd, bool extendedTkhd, int trackId)
    {
        var tkhdPayload = new byte[versionOne ? 96 : 84];
        tkhdPayload[0] = versionOne ? (byte)1 : (byte)0;
        tkhdPayload[3] = 7;
        var trackIdOffset = versionOne ? 20 : 12;
        var trackDurationOffset = versionOne ? 28 : 20;
        BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload.AsSpan(trackIdOffset, 4), (uint)trackId);
        if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(tkhdPayload.AsSpan(trackDurationOffset, 8), trackDuration);
        else BinaryPrimitives.WriteUInt32BigEndian(tkhdPayload.AsSpan(trackDurationOffset, 4), trackDuration);
        var tkhd = Box("tkhd", tkhdPayload, extendedTkhd);
        var mdhdPayload = new byte[versionOne ? 36 : 24];
        mdhdPayload[0] = versionOne ? (byte)1 : (byte)0;
        var mediaTimescaleOffset = versionOne ? 20 : 12;
        var mediaDurationOffset = versionOne ? 24 : 16;
        BinaryPrimitives.WriteUInt32BigEndian(mdhdPayload.AsSpan(mediaTimescaleOffset, 4), timescale);
        if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(mdhdPayload.AsSpan(mediaDurationOffset, 8), mediaDuration);
        else BinaryPrimitives.WriteUInt32BigEndian(mdhdPayload.AsSpan(mediaDurationOffset, 4), mediaDuration);
        var mdhd = Box("mdhd", mdhdPayload, extendedMdhd);
        var hdlrPayload = new byte[32];
        "soun"u8.CopyTo(hdlrPayload.AsSpan(8, 4));
        var hdlr = Box("hdlr", hdlrPayload);
        var stsd = CreateStsd(timescale);
        var sttsPayload = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(sttsPayload.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(sttsPayload.AsSpan(8, 4), sampleCount);
        BinaryPrimitives.WriteUInt32BigEndian(sttsPayload.AsSpan(12, 4), 1);
        var stts = Box("stts", sttsPayload);
        var stbl = Box("stbl", stsd.Concat(stts).ToArray());
        var smhd = Box("smhd", [0, 0, 0, 0, 0, 0, 0, 0]);
        var minf = Box("minf", smhd.Concat(stbl).ToArray());
        var mdia = Box("mdia", mdhd.Concat(hdlr).Concat(minf).ToArray());
        return Box("trak", tkhd.Concat(mdia).ToArray());
    }
    private static byte[] CreateStsd(uint sampleRate)
    {
        var entry = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(), (uint)entry.Length); "mp4a"u8.CopyTo(entry.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(14), 1); BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), 2);
        BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), 16); BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(32), sampleRate << 16);
        var payload = new byte[44];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 1); entry.AsSpan().CopyTo(payload.AsSpan(8));
        return Box("stsd", payload);
    }
    private static byte[] Box(string type, byte[] payload, bool extended = false)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        if (!extended)
        {
            var box = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
            typeBytes.AsSpan().CopyTo(box.AsSpan(4));
            payload.AsSpan().CopyTo(box.AsSpan(8));
            return box;
        }
        var extendedBox = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(extendedBox.AsSpan(0, 4), 1);
        typeBytes.AsSpan().CopyTo(extendedBox.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(extendedBox.AsSpan(8, 8), (ulong)extendedBox.Length);
        payload.AsSpan().CopyTo(extendedBox.AsSpan(16));
        return extendedBox;
    }
    private static int FindBox(byte[] bytes, string type)
    {
        var marker = Encoding.ASCII.GetBytes(type);
        for (var index = 4; index + marker.Length <= bytes.Length; index++)
        {
            if (!bytes.AsSpan(index, marker.Length).SequenceEqual(marker))
                continue;
            var start = index - 4;
            if (start >= 0 && ReadBoxSize(bytes, start) >= 8)
                return start;
        }
        throw new InvalidOperationException($"Box {type} was not found.");
    }
    private static ulong ReadBoxSize(byte[] bytes, int boxStart) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(boxStart, 4)) == 1
        ? BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(boxStart + 8, 8))
        : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(boxStart, 4));
    private static int DurationOffset(byte[] bytes, int boxStart, bool versionOne, bool isTrackHeader)
    {
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(boxStart, 4)) == 1 ? 16 : 8;
        var relativeOffset = isTrackHeader ? (versionOne ? 28 : 20) : (versionOne ? 24 : 16);
        return boxStart + headerLength + relativeOffset;
    }
    private sealed record Mp3Fixture(byte[] Bytes, int FrameStart, int TagStart, int CountOffset, int FrameLength);
    private sealed record Mp4Fixture(byte[] Bytes, int MvhdDurationOffset, int MdhdDurationOffset, int TkhdDurationOffset, int SttsOffset, int SttsLength);
}
