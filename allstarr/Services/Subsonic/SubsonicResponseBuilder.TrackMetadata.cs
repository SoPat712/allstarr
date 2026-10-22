using System.Xml.Linq;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Services.Subsonic;

public partial class SubsonicResponseBuilder
{
    private static void AddTrackMetadata(Song song, Dictionary<string, object> item)
    {
        if (song.IsLocal) return;
        item["path"] = allstarr.Core.Protocols.ExternalTrackPresentation.VirtualPath(song);
        void Number(string key, long? value) { if (value is > 0) item[key] = value.Value; }
        if (song.Size is > 0) item["size"] = song.Size.Value;
        if (song.Bitrate is >= 1000) item["bitRate"] = song.Bitrate.Value / 1000;
        if (song.SampleRate is > 0) item["samplingRate"] = song.SampleRate.Value;
        if (song.BitDepth is > 0) item["bitDepth"] = song.BitDepth.Value;
        if (song.Channels is > 0) item["channelCount"] = song.Channels.Value;
        Number("totalTracks", song.TotalTracks);
        Number("totalDiscs", song.TotalDiscs);
        if (MusicDescriptiveMetadata.ReleaseDate(song.ReleaseDate, song.Year) is { } release)
            item["year"] = release["year"];
        if (TryGetKnownMediaType(string.IsNullOrWhiteSpace(song.Container) ? song.LocalPath : "track." + song.Container,
                out var suffix, out var contentType))
        {
            item["suffix"] = suffix;
            item["contentType"] = contentType;
        }
        if (!string.IsNullOrWhiteSpace(song.Codec) && song.Codec != "unknown") item["codec"] = song.Codec;
        if (!string.IsNullOrWhiteSpace(song.Notes)) item["comment"] = song.Notes;
        if (!string.IsNullOrWhiteSpace(song.Copyright)) item["copyright"] = song.Copyright;
        if (!string.IsNullOrWhiteSpace(song.Label)) item["recordLabels"] = new[] { new { name = song.Label } };
        if (!string.IsNullOrWhiteSpace(song.Upc)) item["upc"] = song.Upc;
        if (!string.IsNullOrWhiteSpace(song.MusicalKey)) item["key"] = song.MusicalKey;
        var genres = (song.Genres.Count > 0 ? song.Genres : string.IsNullOrWhiteSpace(song.Genre) ? [] : new[] { song.Genre }.ToList())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (genres.Length > 0) item["genres"] = genres.Select(name => new Dictionary<string, string> { ["name"] = name }).ToArray();
        if (song.ReplayGain is { } gain)
        {
            var values = new Dictionary<string, double>();
            void Gain(string key, double? value, bool peak = false)
            {
                if (value is { } number && double.IsFinite(number) && (!peak || number >= 0)) values[key] = number;
            }
            Gain("trackGain", gain.TrackGain);
            Gain("albumGain", gain.AlbumGain);
            Gain("trackPeak", gain.TrackPeak, true);
            Gain("albumPeak", gain.AlbumPeak, true);
            if (values.Count > 0) item["replayGain"] = values;
        }
    }

    private static void AddTrackMetadata(Song song, XElement item)
    {
        if (song.IsLocal) return;
        var facts = new Dictionary<string, object>();
        AddTrackMetadata(song, facts);
        foreach (var (key, value) in facts)
        {
            if (key == "replayGain")
                item.Add(new XElement(item.Name.Namespace + key,
                    ((Dictionary<string, double>)value).Select(pair => new XAttribute(pair.Key, pair.Value))));
            else if (key == "recordLabels")
                item.Add(new XElement(item.Name.Namespace + key, new XAttribute("name", song.Label!)));
            else if (key == "genres")
            {
                item.Elements(item.Name.Namespace + key).Remove();
                foreach (var genre in (Dictionary<string, string>[])value)
                    item.Add(new XElement(item.Name.Namespace + key, new XAttribute("name", genre["name"])));
            }
            else item.SetAttributeValue(key, value);
        }
    }
}
