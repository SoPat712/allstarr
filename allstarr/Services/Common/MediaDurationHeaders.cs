using System.Buffers.Binary;

namespace allstarr.Services.Common;

public static class MediaDurationHeaders
{
    private const uint Moov = 0x6d6f6f76, Mdat = 0x6d646174, Moof = 0x6d6f6f66, Mvex = 0x6d766578;
    private const uint Trak = 0x7472616b, Mvhd = 0x6d766864, Tkhd = 0x746b6864, Mdia = 0x6d646961;
    private const uint Mdhd = 0x6d646864, Hdlr = 0x68646c72, Minf = 0x6d696e66, Stbl = 0x7374626c;
    private const uint Stts = 0x73747473, Edts = 0x65647473, Elst = 0x656c7374, Soun = 0x736f756e;

    public static bool TryPatch(Span<byte> prefix, string container, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return false;
        if (string.Equals(container, "flac", StringComparison.OrdinalIgnoreCase) || string.Equals(container, "audio/flac", StringComparison.OrdinalIgnoreCase))
            return PatchFlac(prefix, duration);
        if (string.Equals(container, "mp3", StringComparison.OrdinalIgnoreCase) || string.Equals(container, "audio/mpeg", StringComparison.OrdinalIgnoreCase))
            return PatchMp3(prefix, duration);
        if (string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(container, "m4a", StringComparison.OrdinalIgnoreCase) || string.Equals(container, "audio/mp4", StringComparison.OrdinalIgnoreCase))
            return PatchMp4(prefix, duration);
        return false;
    }

    public static bool TryGetId3TagLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < 10 || !header[..3].SequenceEqual("ID3"u8))
            return false;
        var major = header[3];
        if (major is < 2 or > 4)
            return false;
        var allowed = major == 2 ? 0x80 : major == 3 ? 0xe0 : 0xf0;
        if ((header[5] & ~allowed) != 0 || (header[6] | header[7] | header[8] | header[9]) > 0x7f)
            return false;
        var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        var total = 10L + size + (major == 4 && (header[5] & 0x10) != 0 ? 10 : 0);
        if (total > 1_048_576)
            return false;
        length = (int)total;
        return true;
    }
    private static bool PatchFlac(Span<byte> bytes, TimeSpan duration)
    {
        if (bytes.Length < 42 || !bytes[..4].SequenceEqual("fLaC"u8))
            return false;
        var header = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..8]);
        if ((header & 0x7f000000) != 0 || (header & 0x00ffffff) != 34)
            return false;
        var packed = BinaryPrimitives.ReadUInt64BigEndian(bytes[18..26]);
        var mask = (1UL << 36) - 1;
        if ((packed & mask) != 0 || !Rounded(duration, packed >> 44, 1, mask, out var samples))
            return false;
        BinaryPrimitives.WriteUInt64BigEndian(bytes[18..26], (packed & ~mask) | samples);
        return true;
    }
    private static bool PatchMp3(Span<byte> bytes, TimeSpan duration)
    {
        var source = (ReadOnlySpan<byte>)bytes;
        if (!FindMp3Frame(source, out var frame) || frame > source.Length - 4)
            return false;
        var b1 = source[frame + 1];
        var version = (b1 >> 3) & 3;
        var header = source[frame + 2];
        if (source[frame] != 0xff || (b1 & 0xe0) != 0xe0 || ((b1 >> 1) & 3) != 1 || (b1 & 1) == 0 || version == 1)
            return false;
        var bitrate = Bitrate(version, header >> 4);
        var rate = SampleRate(version, (header >> 2) & 3);
        if (bitrate == 0 || rate == 0)
            return false;
        var length = checked((version == 3 ? 144000 : 72000) * bitrate / rate + ((header >> 1) & 1));
        if (length < 4 || length > source.Length - frame)
            return false;
        var mono = (source[frame + 3] >> 6) == 3;
        var sideInfo = version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17);
        var tag = frame + 4 + sideInfo;
        var samplesPerFrame = version == 3 ? 1152UL : 576UL;
        if (Has(source, tag, "Xing"u8) || Has(source, tag, "Info"u8))
            return PatchXing(bytes, source, tag, frame, length, rate, samplesPerFrame, duration);
        if (Has(source, frame + 36, "VBRI"u8))
            return PatchVbri(bytes, source, frame + 36, frame, length, rate, samplesPerFrame, duration);
        return false;
    }
    private static bool FindMp3Frame(ReadOnlySpan<byte> bytes, out int frame)
    {
        frame = 0;
        if (!Has(bytes, 0, "ID3"u8))
            return bytes.Length >= 4;
        if (!TryGetId3TagLength(bytes[..Math.Min(10, bytes.Length)], out frame))
            return false;
        return frame <= bytes.Length - 4;
    }
    private static bool PatchXing(Span<byte> destination, ReadOnlySpan<byte> source, int tag, int frame, int length, int rate, ulong samplesPerFrame, TimeSpan duration)
    {
        var end = frame + length;
        if (tag > end - 8 || !TryU32(source, tag + 4, out var flags) || (flags & ~0xf) != 0 || (flags & 1) == 0 ||
            !TryU32(source, tag + 8, out var count) || count != 0 || HasEncoderExtension(source[(tag + 8)..end]))
            return false;
        var cursor = tag + 12;
        if ((flags & 2) != 0)
        {
            if (!TryU32(source, cursor, out var byteCount) || (byteCount != 0 && byteCount < length))
                return false;
            cursor += 4;
        }
        if ((flags & 4) != 0)
        {
            if (cursor > end - 100)
                return false;
            cursor += 100;
        }
        if ((flags & 8) != 0)
        {
            if (cursor > end - 4)
                return false;
            cursor += 4;
        }
        if (cursor > end || !Rounded(duration, (ulong)rate, samplesPerFrame, uint.MaxValue, out var frames))
            return false;
        BinaryPrimitives.WriteUInt32BigEndian(destination[(tag + 8)..(tag + 12)], (uint)frames);
        return true;
    }
    private static bool PatchVbri(Span<byte> destination, ReadOnlySpan<byte> source, int tag, int frame, int length, int rate, ulong samplesPerFrame, TimeSpan duration)
    {
        var end = frame + length;
        if (tag > end - 26 || !TryU16(source, tag + 4, out var version) || version != 1 || !TryU32(source, tag + 10, out var byteCount) ||
            (byteCount != 0 && byteCount < length) || !TryU32(source, tag + 14, out var count) || count != 0 ||
            !TryU16(source, tag + 18, out var entries) || !TryU16(source, tag + 22, out var bytesPerEntry))
            return false;
        var tableBytes = (ulong)entries * bytesPerEntry;
        if ((entries != 0 && bytesPerEntry == 0) || tableBytes > (ulong)end - (ulong)(tag + 26) ||
            !Rounded(duration, (ulong)rate, samplesPerFrame, uint.MaxValue, out var frames))
            return false;
        BinaryPrimitives.WriteUInt32BigEndian(destination[(tag + 14)..(tag + 18)], (uint)frames);
        return true;
    }
    private static int Bitrate(int version, int index) => version == 3
        ? index switch { 1 => 32, 2 => 40, 3 => 48, 4 => 56, 5 => 64, 6 => 80, 7 => 96, 8 => 112, 9 => 128, 10 => 160, 11 => 192, 12 => 224, 13 => 256, 14 => 320, _ => 0 }
        : index switch { 1 => 8, 2 => 16, 3 => 24, 4 => 32, 5 => 40, 6 => 48, 7 => 56, 8 => 64, 9 => 80, 10 => 96, 11 => 112, 12 => 128, 13 => 144, 14 => 160, _ => 0 };

    private static int SampleRate(int version, int index) => version switch
    {
        3 => index switch { 0 => 44100, 1 => 48000, 2 => 32000, _ => 0 },
        2 => index switch { 0 => 22050, 1 => 24000, 2 => 16000, _ => 0 },
        0 => index switch { 0 => 11025, 1 => 12000, 2 => 8000, _ => 0 },
        _ => 0
    };

    private static bool PatchMp4(Span<byte> bytes, TimeSpan duration)
    {
        var source = (ReadOnlySpan<byte>)bytes;
        var moov = default(Box);
        var foundMoov = false;
        for (var cursor = 0; cursor < source.Length;)
        {
            if (!TryBox(source, cursor, source.Length, true, out var box))
                return false;
            if (box.Type == Mdat)
                return foundMoov && PrepareMp4(source, moov, duration, out var patch) && ApplyMp4(bytes, patch);
            if (Forbidden(box.Type))
                return false;
            if (box.Type == Moov)
            {
                if (foundMoov)
                    return false;
                foundMoov = true;
                moov = box;
            }
            cursor = box.End;
        }
        return false;
    }

    private static bool PrepareMp4(ReadOnlySpan<byte> source, Box moov, TimeSpan duration, out Mp4Patch patch)
    {
        patch = default;
        if (!Single(source, moov, Mvhd, out var mvhdBox) || !Single(source, moov, Trak, out var trakBox) ||
            !Duration(source, mvhdBox, false, out var mvhd) || !ParseTrack(source, trakBox, out var track) || !track.Audio)
            return false;
        if (!Within(duration, track.Media.Timescale, track.MediaTicks))
            return false;
        var trackDuration = track.Track with { Timescale = mvhd.Timescale };
        if (!Rounded(duration, mvhd.Timescale, 1, mvhd.Width == 4 ? uint.MaxValue : ulong.MaxValue, out var movieTicks) ||
            !Rounded(duration, track.Media.Timescale, 1, track.Media.Width == 4 ? uint.MaxValue : ulong.MaxValue, out var mediaTicks) ||
            !Valid(mvhd, duration) || !Valid(track.Media, duration) || !Valid(trackDuration, duration))
            return false;
        patch = new Mp4Patch(mvhd, track.Media, trackDuration, movieTicks, mediaTicks);
        return true;
    }
    private static bool ParseTrack(ReadOnlySpan<byte> source, Box box, out TrackInfo track)
    {
        track = default;
        if (!Single(source, box, Tkhd, out var tkhdBox) || !Single(source, box, Mdia, out var mdiaBox) ||
            !Duration(source, tkhdBox, true, out var tkhd) || !ParseMedia(source, mdiaBox, out track))
            return false;
        track = track with { Track = tkhd };
        return true;
    }
    private static bool ParseMedia(ReadOnlySpan<byte> source, Box box, out TrackInfo track)
    {
        track = default;
        if (!Single(source, box, Mdhd, out var mdhdBox) || !Single(source, box, Hdlr, out var hdlrBox) || !Single(source, box, Minf, out var minfBox) ||
            hdlrBox.PayloadLength < 12 || U32(source, hdlrBox.Payload + 8) != Soun || !Duration(source, mdhdBox, false, out var mdhd) ||
            !Single(source, minfBox, Stbl, out var stblBox) || !Single(source, stblBox, Stts, out var sttsBox) || !ReadStts(source, sttsBox, out var ticks))
            return false;
        track = new TrackInfo(true, mdhd, default, ticks);
        return true;
    }
    private static bool Single(ReadOnlySpan<byte> source, Box parent, uint wanted, out Box found)
    {
        found = default;
        var seen = false;
        for (var cursor = parent.Payload; cursor < parent.End;)
        {
            if (!TryBox(source, cursor, parent.End, false, out var child) || Forbidden(child.Type))
                return false;
            if (child.Type == wanted)
            {
                if (seen)
                    return false;
                seen = true;
                found = child;
            }
            cursor = child.End;
        }
        return seen;
    }
    private static bool ReadStts(ReadOnlySpan<byte> source, Box box, out ulong ticks)
    {
        ticks = 0;
        if (box.PayloadLength < 8 || source[box.Payload] != 0)
            return false;
        var entries = U32(source, box.Payload + 4);
        if (entries == 0 || 8UL + entries * 8UL != (ulong)box.PayloadLength)
            return false;
        var cursor = box.Payload + 8;
        for (var index = 0U; index < entries; index++, cursor += 8)
        {
            var count = U32(source, cursor);
            var delta = U32(source, cursor + 4);
            if (count == 0 || delta == 0 || (ulong)count > (ulong.MaxValue - ticks) / delta)
                return false;
            ticks += (ulong)count * delta;
        }
        return ticks != 0;
    }
    private static bool Duration(ReadOnlySpan<byte> source, Box box, bool track, out DurationField field)
    {
        field = default;
        if (box.PayloadLength < 4)
            return false;
        var version = source[box.Payload];
        if (version > 1)
            return false;
        var offset = track ? (version == 0 ? 20 : 28) : (version == 0 ? 16 : 24);
        var scaleOffset = version == 0 ? 12 : 20;
        var width = version == 0 ? 4 : 8;
        var minimum = track ? (version == 0 ? 84 : 96) : box.Type == Mvhd ? (version == 0 ? 100 : 112) : (version == 0 ? 24 : 36);
        if (box.PayloadLength < minimum || box.PayloadLength < offset + width)
            return false;
        var scale = track ? 0U : U32(source, box.Payload + scaleOffset);
        if (!track && scale == 0)
            return false;
        field = new DurationField(box.Payload + offset, width, width == 4 ? U32(source, box.Payload + offset) : U64(source, box.Payload + offset), scale);
        return true;
    }
    private static bool ApplyMp4(Span<byte> bytes, Mp4Patch patch) =>
        WriteZero(bytes, patch.Mvhd, patch.MovieTicks) | WriteZero(bytes, patch.Media, patch.MediaTicks) | WriteZero(bytes, patch.Track, patch.MovieTicks);
    private static bool WriteZero(Span<byte> bytes, DurationField field, ulong value)
    {
        if (field.Value != 0 || value == 0)
            return false;
        if (field.Width == 4)
            BinaryPrimitives.WriteUInt32BigEndian(bytes[field.Offset..(field.Offset + 4)], (uint)value);
        else
            BinaryPrimitives.WriteUInt64BigEndian(bytes[field.Offset..(field.Offset + 8)], value);
        return true;
    }
    private static bool Valid(DurationField field, TimeSpan duration) => field.Value == 0 || Within(duration, field.Timescale, field.Value);
    private static bool Rounded(TimeSpan duration, ulong numerator, ulong denominator, ulong maximum, out ulong value)
    {
        value = 0;
        if (numerator == 0 || denominator == 0)
            return false;
        var rounded = decimal.Round((decimal)duration.Ticks * numerator / TimeSpan.TicksPerSecond / denominator, 0, MidpointRounding.AwayFromZero);
        if (rounded <= 0 || rounded > maximum)
            return false;
        value = (ulong)rounded;
        return true;
    }
    private static bool Within(TimeSpan duration, uint scale, ulong ticks)
    {
        if (scale == 0)
            return false;
        var expected = (decimal)duration.Ticks * scale / TimeSpan.TicksPerSecond;
        return Math.Abs(expected - ticks) <= 1;
    }
    private static bool TryBox(ReadOnlySpan<byte> bytes, int start, int limit, bool allowShortMdat, out Box box)
    {
        box = default;
        if (start < 0 || start > limit - 8)
            return false;
        var size32 = U32(bytes, start);
        var type = U32(bytes, start + 4);
        var header = 8;
        ulong size = size32;
        if (size32 == 1)
        {
            if (start > limit - 16)
                return false;
            size = U64(bytes, start + 8);
            header = 16;
        }
        if (size < (ulong)header || size > int.MaxValue)
            return false;
        var end = (ulong)start + size;
        if (end > (ulong)limit)
        {
            if (!allowShortMdat || type != Mdat)
                return false;
            end = (ulong)limit;
        }
        box = new Box(start, start + header, (int)end, type);
        return true;
    }
    private static bool Forbidden(uint type) => type is Moof or Mvex or Edts or Elst;
    private static bool Has(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> marker) => offset >= 0 && offset <= bytes.Length - marker.Length && bytes.Slice(offset, marker.Length).SequenceEqual(marker);
    private static bool Contains(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> marker)
    {
        for (var i = 0; i <= bytes.Length - marker.Length; i++)
            if (bytes.Slice(i, marker.Length).SequenceEqual(marker)) return true;
        return false;
    }
    private static bool HasEncoderExtension(ReadOnlySpan<byte> bytes) => Contains(bytes, "LAME"u8) || Contains(bytes, "Lavc"u8) || Contains(bytes, "Lavf"u8);
    private static bool TryU16(ReadOnlySpan<byte> bytes, int offset, out ushort value)
    {
        if (offset < 0 || offset > bytes.Length - 2) { value = 0; return false; }
        value = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..(offset + 2)]); return true;
    }
    private static bool TryU32(ReadOnlySpan<byte> bytes, int offset, out uint value)
    {
        if (offset < 0 || offset > bytes.Length - 4) { value = 0; return false; }
        value = U32(bytes, offset); return true;
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..(offset + 4)]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64BigEndian(bytes[offset..(offset + 8)]);
    private readonly record struct Box(int Start, int Payload, int End, uint Type) { public int PayloadLength => End - Payload; }
    private readonly record struct DurationField(int Offset, int Width, ulong Value, uint Timescale);
    private readonly record struct TrackInfo(bool Audio, DurationField Media, DurationField Track, ulong MediaTicks);
    private readonly record struct Mp4Patch(DurationField Mvhd, DurationField Media, DurationField Track, ulong MovieTicks, ulong MediaTicks);
}
