using allstarr.Core.Caching;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Core.Matching;
using allstarr.Core.Settings;

namespace allstarr.Core.ClientApiV2;

public sealed class LyricsTranslation(IHttpClientFactory clients, IApplicationCache cache, IDurableRuntimeSettings settings,
    ApplicationCacheRequestCoalescer requests)
{
    public const string HttpClientName = "LyricsTranslation";
    private static readonly IReadOnlyDictionary<string, HashSet<string>> LanguageWords = new Dictionary<string, string>
    {
        ["en"] = "i you he she we they the a an and or but my your our their this that these those is are was were be been have has do does don't can will would in on at with for from of to it it's me i'm all go get",
        ["es"] = "yo tu el ella nosotros ellos las los una unos unas del que y pero mi mis tus sus es son estoy esta estamos eres soy por para con sin como quiero tengo cuando donde porque noche despues dos tres cuatro uno",
        ["pt"] = "eu voce ele ela nos eles elas uma uns umas os as do da dos das que e mas meu minha seu sua sao estou esta somos sou por para com sem como quero tenho quando onde porque nao voce",
        ["fr"] = "je tu il elle nous vous ils elles le la les un une des du et ou mais mon ma mes ton ta tes est sont suis sommes avec sans pour dans sur pas ne que qui quand pourquoi",
        ["de"] = "ich du er sie wir ihr der die das ein eine und oder aber mein meine dein deine ist sind bin bist mit ohne fur von zu auf nicht dass was wenn warum",
        ["it"] = "io tu lui lei noi voi loro gli una uno dei delle che e ma mio mia tuo tua sono sei siamo con senza per come voglio quando dove perche non"
    }.ToDictionary(pair => pair.Key, pair => Words(pair.Value).ToHashSet(StringComparer.Ordinal));

    public static bool TryLanguage(string? value, out string language)
    {
        language = value ?? "en";
        if (language.Length > 100 || !Regex.IsMatch(language,
            @"\A[a-zA-Z]{2,3}(?:-[a-zA-Z]{3}){0,3}(?:-[a-zA-Z]{4})?(?:-(?:[a-zA-Z]{2}|[0-9]{3}))?(?:-(?:[a-zA-Z0-9]{5,8}|[0-9][a-zA-Z0-9]{3}))*(?:-[0-9a-wy-zA-WY-Z](?:-[a-zA-Z0-9]{2,8})+)*(?:-x(?:-[a-zA-Z0-9]{1,8})+)?\z")) return false;
        var primary = language.Split('-')[0];
        if (!CultureInfo.GetCultures(CultureTypes.NeutralCultures).Any(culture =>
            culture.TwoLetterISOLanguageName.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
            culture.ThreeLetterISOLanguageName.Equals(primary, StringComparison.OrdinalIgnoreCase))) return false;
        var parts = language.ToLowerInvariant().Split('-');
        for (var index = 1; index < parts.Length; index++)
        {
            if (parts[index].Length == 1) break;
            if (parts[index].Length == 2) parts[index] = parts[index].ToUpperInvariant();
            else if (parts[index].Length == 4 && parts[index].All(char.IsAsciiLetter))
                parts[index] = char.ToUpperInvariant(parts[index][0]) + parts[index][1..];
        }
        language = string.Join('-', parts);
        return true;
    }

    public async Task<V2LyricsTranslationResponse> TranslateAsync(string trackId, IReadOnlyList<string> lines, string to, CancellationToken token)
    {
        var key = $"lyrics:translation:v2:{trackId}:{CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(lines))}:{to}";
        return await requests.RunAsync(key, async () =>
        {
            if (await cache.GetAsync<V2LyricsTranslationResponse>(key) is { } cached) return cached;
            var selected = SelectLines(lines, to);
            Translation[]? translated = null;
            string? provider = null;
            if (selected.Length > 0)
            {
                var url = (await settings.GetAsync("Lyrics:Translation:LibreTranslateUrl", token)).NormalizedValue;
                foreach (var name in new[] { "google", "mymemory", "libretranslate" })
                {
                    if (name == "libretranslate" && string.IsNullOrEmpty(url)) continue;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        translated = await TranslateWithAsync(name, url, selected.Select(index => lines[index]).ToArray(), to, timeout.Token);
                    }
                    catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException ||
                        error is OperationCanceledException && !token.IsCancellationRequested)
                    { }
                    if (translated == null) continue;
                    provider = name;
                    break;
                }
            }
            var output = lines.Select((line, index) => new V2LyricsTranslationLine(index, line, null, null)).ToArray();
            if (translated != null)
                for (var index = 0; index < selected.Length; index++)
                {
                    var position = selected[index];
                    var result = translated[index];
                    output[position] = output[position] with
                    {
                        Text = WorthShowing(lines[position], result.Text) ? result.Text : null,
                        From = result.From
                    };
                }
            var response = new V2LyricsTranslationResponse(trackId, to, provider, output);
            await cache.SetAsync(key, response, TimeSpan.FromDays(30));
            return response;
        }, token);
    }

    private static int[] SelectLines(IReadOnlyList<string> lines, string to)
    {
        // Hints only decide what to send; source detection always belongs to the translation service.
        var words = lines.Select(Words).ToArray();
        var target = LanguageWords.GetValueOrDefault(to.Split('-')[0]) ?? [];
        var other = LanguageWords.Where(pair => pair.Key != to.Split('-')[0]).SelectMany(pair => pair.Value)
            .Except(target).ToHashSet(StringComparer.Ordinal);
        var clearTarget = words.Select(line => line.Count(target.Contains) >= 2 && !line.Any(other.Contains)).ToArray();
        var clearForeign = words.Select(line => !line.Any(target.Contains) || line.Any(other.Contains)).ToArray();
        var targetWords = words.Where((_, index) => clearTarget[index]).SelectMany(line => line).ToHashSet(StringComparer.Ordinal);
        var foreignWords = words.Where((_, index) => clearForeign[index])
            .SelectMany(line => line).Except(targetWords).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, lines.Count).Where(index => words[index].Length > 0 &&
            (clearForeign[index] || words[index].Any(foreignWords.Contains) || !clearTarget[index] && words[index].Length > 3)).ToArray();
    }

    private async Task<Translation[]?> TranslateWithAsync(string provider, string url, string[] lines, string to, CancellationToken token)
    {
        using var client = clients.CreateClient(HttpClientName);
        if (provider != "mymemory")
        {
            var song = await SendAsync(string.Join('\n', lines));
            if (song == null) return null;
            var split = song.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            if (split.Length == lines.Length) return split.Select(text => new Translation(text, song.From)).ToArray();
        }
        var translated = new List<Translation>();
        foreach (var line in lines)
        {
            if (provider == "mymemory" && line.Length > 500)
            {
                translated.Add(new("", null));
                continue;
            }
            var result = await SendAsync(line);
            if (result == null) return null;
            translated.Add(result);
        }
        return translated.Any(line => !string.IsNullOrWhiteSpace(line.Text)) ? translated.ToArray() : null;

        async Task<Translation?> SendAsync(string text)
        {
            using var request = provider switch
            {
                "google" => new HttpRequestMessage(HttpMethod.Get,
                    "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=" + Uri.EscapeDataString(to) + "&dt=t&q=" + Uri.EscapeDataString(text)),
                "mymemory" => new HttpRequestMessage(HttpMethod.Get,
                    "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString("autodetect|" + to)),
                _ => new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/translate")
                { Content = JsonContent.Create(new { q = text, source = "auto", target = to, format = "text" }) }
            };
            using var response = await client.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = json.RootElement;
            if (provider == "google")
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array) return null;
                var segments = root[0].EnumerateArray().ToArray();
                if (segments.Length == 0 || segments.Any(segment => segment.ValueKind != JsonValueKind.Array ||
                    segment.GetArrayLength() == 0 || segment[0].ValueKind != JsonValueKind.String)) return null;
                return new(string.Concat(segments.Select(segment => segment[0].GetString())),
                    root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() : null);
            }
            if (provider == "mymemory")
            {
                if (root.TryGetProperty("responseStatus", out var status) && status.ToString() != "200") return null;
                if (!root.TryGetProperty("responseData", out root)) return null;
            }
            if (!root.TryGetProperty("translatedText", out var translated) || translated.ValueKind != JsonValueKind.String) return null;
            string? detected = null;
            if (root.TryGetProperty("detectedLanguage", out var language))
                detected = language.ValueKind == JsonValueKind.String ? language.GetString() :
                    language.ValueKind == JsonValueKind.Object && language.TryGetProperty("language", out var name) &&
                    name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            return new(translated.GetString()!, detected);
        }
    }

    private static string[] Words(string text)
    {
        var normalized = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            normalized.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool WorthShowing(string source, string translated)
    {
        var before = Words(source);
        var after = Words(translated);
        if (after.Length == 0 || string.Concat(before) == string.Concat(after)) return false;
        if (before.Length < 3 || before.Length != after.Length) return true;
        var changed = -1;
        for (var index = 0; index < before.Length; index++)
        {
            if (before[index] == after[index]) continue;
            if (changed >= 0) return true;
            changed = index;
        }
        if (changed < 0) return false;
        var left = before[changed];
        var right = after[changed];
        var limit = Math.Min(left.Length, right.Length) / 4;
        if (Math.Abs(left.Length - right.Length) > limit) return true;
        var distances = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var row = 1; row <= left.Length; row++)
        {
            var diagonal = distances[0];
            distances[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var previous = distances[column];
                distances[column] = Math.Min(Math.Min(distances[column] + 1, distances[column - 1] + 1),
                    diagonal + (left[row - 1] == right[column - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return distances[right.Length] > limit;
    }

    private sealed record Translation(string Text, string? From);
}
