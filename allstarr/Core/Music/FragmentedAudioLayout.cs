using System.Buffers.Binary;
using System.Text;

namespace allstarr.Core.Music;

internal sealed record FragmentedAudioLayout(int InitializationLength, double Duration, int SampleRate, int? Channels)
{
    public static FragmentedAudioLayout Read(byte[] bytes)
    {
        try
        {
            var boxes = Boxes(bytes, 0, bytes.Length);
            var ftyp = One(boxes, "ftyp");
            var compatible = false;
            for (var offset = ftyp.Payload + 8; offset + 4 <= ftyp.End; offset += 4)
                compatible |= Encoding.ASCII.GetString(bytes, offset, 4) == "iso6";
            if (!compatible) throw Unsupported();
            var moov = One(boxes, "moov");
            var movie = Boxes(bytes, moov.Payload, moov.End);
            var mvhd = One(movie, "mvhd");
            if (ReadDuration(bytes, mvhd, 16, 24) != 0) throw Unsupported();
            var trak = One(movie, "trak");
            var track = Boxes(bytes, trak.Payload, trak.End);
            if (track.Any(box => box.Type == "edts")) throw Unsupported();
            var tkhd = One(track, "tkhd");
            if (ReadDuration(bytes, tkhd, 20, 28) != 0) throw Unsupported();
            var trackId = UInt(bytes, tkhd, Version(bytes, tkhd) == 0 ? 12 : 20);
            var mdia = One(track, "mdia");
            var media = Boxes(bytes, mdia.Payload, mdia.End);
            var mdhd = One(media, "mdhd");
            var timescale = UInt(bytes, mdhd, Version(bytes, mdhd) == 0 ? 12 : 20);
            if (timescale == 0) throw Unsupported();
            var hdlr = One(media, "hdlr");
            if (UInt(bytes, hdlr, 8) != 0x736f756e) throw Unsupported();
            var minf = One(media, "minf");
            var stbl = One(Boxes(bytes, minf.Payload, minf.End), "stbl");
            var table = Boxes(bytes, stbl.Payload, stbl.End);
            var stsd = One(table, "stsd");
            if (UInt(bytes, stsd, 4) != 1) throw Unsupported();
            var sampleEntry = One(Boxes(bytes, stsd.Payload + 8, stsd.End), "mp4a");
            if (sampleEntry.Size < 36) throw Unsupported();
            var esds = One(Boxes(bytes, sampleEntry.Payload + 28, sampleEntry.End), "esds");
            var audio = ReadAac(bytes, esds);
            foreach (var type in new[] { "stts", "stsc", "stco" })
                if (UInt(bytes, One(table, type), 4) != 0) throw Unsupported();
            if (UInt(bytes, One(table, "stsz"), 8) != 0) throw Unsupported();
            var mvex = One(movie, "mvex");
            var trex = One(Boxes(bytes, mvex.Payload, mvex.End), "trex");
            if (UInt(bytes, trex, 4) != trackId) throw Unsupported();
            var defaultDuration = UInt(bytes, trex, 12);
            var defaultSize = UInt(bytes, trex, 16);
            var fragments = boxes.Where(box => box.Type == "moof").ToArray();
            if (fragments.Length == 0 || boxes.Count(box => box.Type == "mdat") != fragments.Length ||
                boxes[0] != ftyp || moov.End > fragments[0].Start ||
                boxes.Any(box => box.Type == "mdat" && box.Start < fragments[0].Start)) throw Unsupported();
            ulong ticks = 0;
            foreach (var fragment in fragments)
            {
                var index = boxes.IndexOf(fragment);
                if (index + 1 >= boxes.Count || boxes[index + 1].Type != "mdat") throw Unsupported();
                var traf = One(Boxes(bytes, fragment.Payload, fragment.End), "traf");
                var entries = Boxes(bytes, traf.Payload, traf.End);
                var tfhd = One(entries, "tfhd");
                var flags = UInt(bytes, tfhd, 0) & 0xffffff;
                if ((flags & 0x020001) != 0x020000 || (flags & 0x010000) != 0 || UInt(bytes, tfhd, 4) != trackId)
                    throw Unsupported();
                var durationOffset = (flags & 2) != 0 ? 12 : 8;
                var duration = (flags & 8) != 0 ? UInt(bytes, tfhd, durationOffset) : defaultDuration;
                var size = (flags & 0x10) != 0 ? UInt(bytes, tfhd, durationOffset + ((flags & 8) != 0 ? 4 : 0)) : defaultSize;
                var mdat = boxes[index + 1];
                var dataPosition = (long)mdat.Payload;
                var tfdt = One(entries, "tfdt");
                if (ReadDuration(bytes, tfdt, 4, 4) != ticks) throw Unsupported();
                var runs = entries.Where(box => box.Type == "trun").ToArray();
                if (runs.Length == 0) throw Unsupported();
                foreach (var run in runs)
                {
                    var runFlags = UInt(bytes, run, 0) & 0xffffff;
                    var count = UInt(bytes, run, 4);
                    if (count == 0 || count > 1000000 || (runFlags & ~0xf05u) != 0 || (runFlags & 1) == 0 ||
                        (long)fragment.Start + UInt(bytes, run, 8) != dataPosition) throw Unsupported();
                    var offset = 8 + ((runFlags & 1) != 0 ? 4 : 0) + ((runFlags & 4) != 0 ? 4 : 0);
                    var stride = ((runFlags & 0x100) != 0 ? 4 : 0) + ((runFlags & 0x200) != 0 ? 4 : 0) +
                        ((runFlags & 0x400) != 0 ? 4 : 0) + ((runFlags & 0x800) != 0 ? 4 : 0);
                    if ((long)run.Payload + offset + (long)stride * count > run.End) throw Unsupported();
                    if ((runFlags & 0x100) == 0)
                    {
                        if (duration == 0) throw Unsupported();
                        ticks = checked(ticks + (ulong)count * duration);
                    }
                    if ((runFlags & 0x200) == 0)
                    {
                        if (size == 0) throw Unsupported();
                        dataPosition = checked(dataPosition + (long)count * size);
                    }
                    if ((runFlags & 0x300) != 0)
                    {
                        for (var sample = 0; sample < count; sample++)
                        {
                            if ((runFlags & 0x100) != 0)
                            {
                                var sampleDuration = UInt(bytes, run, offset);
                                if (sampleDuration == 0) throw Unsupported();
                                ticks = checked(ticks + sampleDuration);
                            }
                            if ((runFlags & 0x200) != 0)
                                dataPosition = checked(dataPosition + UInt(bytes, run, offset + ((runFlags & 0x100) != 0 ? 4 : 0)));
                            offset += stride;
                        }
                    }
                    if (dataPosition > mdat.End) throw Unsupported();
                }
                if (dataPosition != mdat.End) throw Unsupported();
            }
            var seconds = (double)ticks / timescale;
            if (seconds <= 0 || seconds > 86400) throw Unsupported();
            return new(fragments[0].Start, seconds, audio.SampleRate, audio.Channels);
        }
        catch (Exception error) when (error is OverflowException or ArgumentException)
        {
            throw Unsupported();
        }
    }

    private static (int SampleRate, int? Channels) ReadAac(byte[] bytes, Box esds)
    {
        var offset = esds.Payload + 4;
        var esEnd = Descriptor(bytes, ref offset, esds.End, 3);
        if (offset + 3 > esEnd) throw Unsupported();
        var flags = bytes[offset + 2];
        offset += 3;
        if ((flags & 0x80) != 0) offset += 2;
        if ((flags & 0x40) != 0)
        {
            if (offset >= esEnd) throw Unsupported();
            offset += 1 + bytes[offset];
        }
        if ((flags & 0x20) != 0) offset += 2;
        var decoderEnd = Descriptor(bytes, ref offset, esEnd, 4);
        if (offset + 13 > decoderEnd || bytes[offset] != 0x40 || (bytes[offset + 1] >> 2) != 5)
            throw Unsupported();
        offset += 13;
        var configEnd = Descriptor(bytes, ref offset, decoderEnd, 5);
        var bit = offset * 8;
        var objectType = Bits(bytes, ref bit, configEnd, 5);
        if (objectType is not (2 or 5 or 29)) throw Unsupported();
        var sampleRate = ReadSampleRate(bytes, ref bit, configEnd);
        var channels = Bits(bytes, ref bit, configEnd, 4);
        if (objectType is 5 or 29)
        {
            sampleRate = ReadSampleRate(bytes, ref bit, configEnd);
            if (Bits(bytes, ref bit, configEnd, 5) != 2) throw Unsupported();
        }
        return (sampleRate, channels is >= 1 and <= 6 ? channels : channels == 7 ? 8 : null);
    }

    private static int ReadSampleRate(byte[] bytes, ref int bit, int end)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        var index = Bits(bytes, ref bit, end, 4);
        var rate = index == 15 ? Bits(bytes, ref bit, end, 24) : index < rates.Length ? rates[index] : 0;
        return rate > 0 ? rate : throw Unsupported();
    }

    private static int Bits(byte[] bytes, ref int bit, int end, int count)
    {
        if ((long)bit + count > (long)end * 8) throw Unsupported();
        var result = 0;
        for (var index = 0; index < count; index++, bit++)
            result = (result << 1) | ((bytes[bit / 8] >> (7 - bit % 8)) & 1);
        return result;
    }

    private static int Descriptor(byte[] bytes, ref int offset, int end, byte tag)
    {
        if (offset >= end || bytes[offset++] != tag) throw Unsupported();
        var length = 0;
        for (var count = 0; count < 4; count++)
        {
            if (offset >= end) throw Unsupported();
            var value = bytes[offset++];
            length = (length << 7) | (value & 0x7f);
            if ((value & 0x80) != 0) continue;
            if (length > end - offset) throw Unsupported();
            return offset + length;
        }
        throw Unsupported();
    }

    private static List<Box> Boxes(byte[] bytes, int start, int end)
    {
        var result = new List<Box>();
        while (start < end)
        {
            if (end - start < 8 || result.Count >= 65536) throw Unsupported();
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start, 4));
            var header = 8;
            if (size == 1)
            {
                if (end - start < 16) throw Unsupported();
                size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start + 8, 8));
                header = 16;
            }
            else if (size == 0) size = (ulong)(end - start);
            if (size < (ulong)header || size > (ulong)(end - start)) throw Unsupported();
            result.Add(new(start, (int)size, header, Encoding.ASCII.GetString(bytes, start + 4, 4)));
            start += (int)size;
        }
        return result;
    }

    private static Box One(List<Box> boxes, string type)
    {
        var matches = boxes.Where(box => box.Type == type).ToArray();
        return matches.Length == 1 ? matches[0] : throw Unsupported();
    }

    private static byte Version(byte[] bytes, Box box)
    {
        var version = UInt(bytes, box, 0) >> 24;
        return version <= 1 ? (byte)version : throw Unsupported();
    }

    private static uint UInt(byte[] bytes, Box box, int offset)
    {
        if (offset < 0 || box.Payload + offset > box.End - 4) throw Unsupported();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(box.Payload + offset, 4));
    }

    private static ulong ReadDuration(byte[] bytes, Box box, int narrowOffset, int wideOffset)
    {
        if (Version(bytes, box) == 0) return UInt(bytes, box, narrowOffset);
        if (box.Payload + wideOffset > box.End - 8) throw Unsupported();
        return BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(box.Payload + wideOffset, 8));
    }

    private static ProviderAudioProfileException Unsupported() => new(
        "HLS requires unencrypted, contiguous fragmented MP4 AAC audio; converting this source is unavailable.");

    private readonly record struct Box(int Start, int Size, int Header, string Type)
    {
        public int Payload => Start + Header;
        public int End => Start + Size;
    }
}
