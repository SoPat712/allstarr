using System.Globalization;
using System.Text.Json;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Music;

public static class ProviderAudioDeliveryRequests
{
    public static ProviderAudioDeliveryRequest Jellyfin(IQueryCollection query, JsonElement? body = null,
        string? container = null, bool universal = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query) values[pair.Key] = pair.Value.ToString();
        if (body is { ValueKind: JsonValueKind.Object } json)
            foreach (var property in json.EnumerateObject())
                if (property.Value.ValueKind != JsonValueKind.Object && property.Value.ValueKind != JsonValueKind.Array)
                {
                    var value = property.Value.ToString();
                    values[property.Name] = (property.Name.StartsWith("Max", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("AudioBitRate", StringComparison.OrdinalIgnoreCase)) &&
                        Positive(values.GetValueOrDefault(property.Name, "")) is { } previous && Positive(value) is { } current
                            ? Math.Min(previous, current).ToString(CultureInfo.InvariantCulture) : value;
                }
        var profile = Property(body, "DeviceProfile");
        var profiles = Property(profile, "DirectPlayProfiles");
        IReadOnlyList<ProviderAudioProfile>? direct = null;
        if (profile.HasValue) direct = [];
        if (profiles is { ValueKind: JsonValueKind.Array } entries)
            direct = entries.EnumerateArray().Where(entry => Text(entry, "Type").Equals("Audio", StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ProviderAudioProfile(Split(Text(entry, "Container")), Split(Text(entry, "AudioCodec")), ["http"],
                    (int?)Positive(Text(entry, "MaxAudioChannels")))).ToArray();
        IReadOnlyList<ProviderAudioProfile>? transcoding = null;
        if (Property(profile, "TranscodingProfiles") is { ValueKind: JsonValueKind.Array } targets)
            transcoding = targets.EnumerateArray().Where(entry => Text(entry, "Type").Equals("Audio", StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ProviderAudioProfile(Split(Text(entry, "Container")), Split(Text(entry, "AudioCodec")),
                    string.IsNullOrWhiteSpace(Text(entry, "Protocol")) ? ["http"] : Split(Text(entry, "Protocol")),
                    (int?)Positive(Text(entry, "MaxAudioChannels")))).ToArray();
        IReadOnlyList<ProviderAudioProfile>? formats = null;
        var directContainer = universal && !string.IsNullOrWhiteSpace(Value(values, "Container"));
        var expression = container ?? Value(values, "Container");
        if (string.IsNullOrWhiteSpace(expression) && universal) expression = Value(values, "TranscodingContainer");
        if (!string.IsNullOrWhiteSpace(expression))
        {
            var codec = Value(values, "AudioCodec");
            formats = expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value =>
            {
                var pair = value.Split('|', 2);
                return new ProviderAudioProfile([pair[0]], pair.Length == 2 ? Split(pair[1]) :
                    directContainer ? [] : Split(codec), ["http"]);
            }).ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(Value(values, "AudioCodec")))
            formats = [new([], Split(Value(values, "AudioCodec")), ["http"])];
        var conditions = new List<ProviderAudioCondition>();
        if (Property(profile, "CodecProfiles") is { ValueKind: JsonValueKind.Array } codecs)
            foreach (var codec in codecs.EnumerateArray().Where(entry => Text(entry, "Type").Equals("Audio", StringComparison.OrdinalIgnoreCase)))
                if (Property(codec, "Conditions") is { ValueKind: JsonValueKind.Array } constraints)
                    conditions.AddRange(constraints.EnumerateArray().Select(condition => new ProviderAudioCondition(
                        Text(codec, "Codec"), Text(condition, "Property"), Text(condition, "Condition"), [Text(condition, "Value")],
                        !Text(condition, "IsRequired").Equals("false", StringComparison.OrdinalIgnoreCase))));
        var error = InvalidNumbers(values, ["MaxStreamingBitrate", "AudioBitRate", "MaxAudioSampleRate", "AudioSampleRate", "MaxAudioChannels", "AudioChannels", "MaxAudioBitDepth", "StartTimeTicks"]);
        return new()
        {
            Profiles = direct,
            TranscodingProfiles = transcoding,
            AllowHls = true,
            Formats = formats,
            Conditions = conditions,
            MaxBitrate = Minimum(Number(values, "MaxStreamingBitrate"), Number(values, "AudioBitRate"), Positive(Text(profile, "MaxStreamingBitrate")), Positive(Text(profile, "MaxStaticBitrate"))),
            MaxSampleRate = Limit(values, "MaxAudioSampleRate"),
            SampleRate = Limit(values, "AudioSampleRate"),
            MaxChannels = Limit(values, "MaxAudioChannels"),
            Channels = Limit(values, "AudioChannels"),
            MaxBitDepth = Limit(values, "MaxAudioBitDepth"),
            StartTimeTicks = Number(values, "StartTimeTicks") ?? 0,
            InvalidTimeOffset = values.TryGetValue("StartTimeTicks", out var start) && start.Length > 0 &&
                (!long.TryParse(start, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks < 0),
            EnableDirectPlay = !Value(values, "EnableDirectPlay").Equals("false", StringComparison.OrdinalIgnoreCase),
            EnableDirectStream = !Value(values, "EnableDirectStream").Equals("false", StringComparison.OrdinalIgnoreCase),
            EnableTranscoding = !Value(values, "EnableTranscoding").Equals("false", StringComparison.OrdinalIgnoreCase) &&
                !Value(values, "EnableAudioTranscoding").Equals("false", StringComparison.OrdinalIgnoreCase),
            Error = error
        };
    }

    public static ProviderAudioDeliveryRequest Subsonic(SubsonicRequestParameters parameters, bool decision = false)
    {
        var values = parameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        JsonElement? body = null;
        if (decision && !string.IsNullOrWhiteSpace(parameters.RawBody))
        {
            try { using var doc = JsonDocument.Parse(parameters.RawBody); body = doc.RootElement.Clone(); }
            catch (JsonException) { return new() { Error = "Invalid audio profile." }; }
        }
        IReadOnlyList<ProviderAudioProfile>? direct = null;
        if (Property(body, "directPlayProfiles") is { ValueKind: JsonValueKind.Array } profiles)
            direct = profiles.EnumerateArray().Select(entry => new ProviderAudioProfile(
                Strings(entry, "containers"), Strings(entry, "audioCodecs"), Strings(entry, "protocols"),
                (int?)Positive(Text(entry, "maxAudioChannels")))).ToArray();
        IReadOnlyList<ProviderAudioProfile>? transcoding = null;
        if (Property(body, "transcodingProfiles") is { ValueKind: JsonValueKind.Array } targets)
            transcoding = targets.EnumerateArray().Select(entry => new ProviderAudioProfile(
                Split(Text(entry, "container")), Split(Text(entry, "audioCodec")),
                string.IsNullOrWhiteSpace(Text(entry, "protocol")) ? ["http"] : Split(Text(entry, "protocol")))).ToArray();
        var format = parameters.GetValueOrDefault("format");
        var audioCodec = parameters.GetValueOrDefault("audioCodec");
        if (!decision && format.Length > 0 && !format.Equals("raw", StringComparison.OrdinalIgnoreCase))
            direct = [new([format], Split(audioCodec), ["http"])];
        else if (!decision && audioCodec.Length > 0 && !format.Equals("raw", StringComparison.OrdinalIgnoreCase))
            direct = [new([], Split(audioCodec), ["http"])];
        var conditions = new List<ProviderAudioCondition>();
        if (Property(body, "codecProfiles") is { ValueKind: JsonValueKind.Array } codecs)
            foreach (var codec in codecs.EnumerateArray().Where(entry => Text(entry, "type").Equals("AudioCodec", StringComparison.OrdinalIgnoreCase)))
                if (Property(codec, "limitations") is { ValueKind: JsonValueKind.Array } limitations)
                    conditions.AddRange(limitations.EnumerateArray().Select(condition => new ProviderAudioCondition(
                        Text(codec, "name"), Text(condition, "name"), Text(condition, "comparison"), Strings(condition, "values"),
                        !Text(condition, "required").Equals("false", StringComparison.OrdinalIgnoreCase))));
        var error = InvalidNumbers(values, ["maxBitRate", "maxAudioBitrate", "maxAudioChannels", "maxAudioSampleRate", "offset", "timeOffset"]);
        var offset = parameters.GetValueOrDefault("timeOffset", parameters.GetValueOrDefault("offset", "0"));
        long ticks = 0;
        var invalidOffset = !decimal.TryParse(offset, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0 || seconds > long.MaxValue / TimeSpan.TicksPerSecond;
        if (invalidOffset) error = "Invalid audio time offset.";
        else ticks = (long)(seconds * TimeSpan.TicksPerSecond);
        return new()
        {
            Profiles = direct,
            TranscodingProfiles = transcoding,
            PreferredCodec = "mp3",
            EnableTranscoding = !format.Equals("raw", StringComparison.OrdinalIgnoreCase),
            Conditions = conditions,
            MaxBitrate = Minimum(Number(values, "maxBitRate") is { } kbps && kbps <= long.MaxValue / 1000 ? kbps * 1000 : null,
                Number(values, "maxAudioBitrate")),
            MaxChannels = Limit(values, "maxAudioChannels"),
            MaxSampleRate = Limit(values, "maxAudioSampleRate"),
            StartTimeTicks = ticks,
            InvalidTimeOffset = invalidOffset,
            Error = error
        };
    }

    private static JsonElement? Property(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } obj
        ? obj.EnumerateObject().Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(property => (JsonElement?)property.Value).FirstOrDefault() : null;
    private static string Text(JsonElement? element, string name) => Property(element, name)?.ToString() ?? string.Empty;
    private static string[] Strings(JsonElement? element, string name) => Property(element, name) is { ValueKind: JsonValueKind.Array } array
        ? array.EnumerateArray().Select(value => value.ToString()).ToArray() : Split(Text(element, name));
    private static string[] Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Value(IReadOnlyDictionary<string, string> values, string key) => values.GetValueOrDefault(key, "");
    private static long? Positive(string value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    private static long? Number(IReadOnlyDictionary<string, string> values, string key) => Positive(Value(values, key));
    private static int? Limit(IReadOnlyDictionary<string, string> values, string key) =>
        Number(values, key) is { } number && number <= int.MaxValue ? (int)number : null;
    private static long? Minimum(params long?[] values) => values.Where(value => value > 0).DefaultIfEmpty(null).Min();
    private static string? InvalidNumbers(IReadOnlyDictionary<string, string> values, string[] keys) => keys.Any(key =>
        values.TryGetValue(key, out var value) && value.Length > 0 &&
        (key is "offset" or "timeOffset"
            ? !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > long.MaxValue / TimeSpan.TicksPerSecond
            : !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0 ||
                key.Contains("Channels", StringComparison.OrdinalIgnoreCase) && number > int.MaxValue ||
                key.Contains("SampleRate", StringComparison.OrdinalIgnoreCase) && number > int.MaxValue ||
                key.Contains("BitDepth", StringComparison.OrdinalIgnoreCase) && number > int.MaxValue))
        ? "Invalid audio limit or time offset." : null;
}
