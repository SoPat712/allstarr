using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;
using allstarr.Services.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionNamingDetectedExample(string Path);
public sealed record PromotionNamingDetection(string? Script, int Matched, int Sampled,
    PromotionNamingDetectedExample[] Examples, string? Reason);

public sealed class PromotionNamingDetectionService(IDbContextFactory<AllstarrDbContext> factory, PromotionProfiles profiles,
    PromotionRoots roots, IBackendLibraryAccessResolver libraryAccess, JellyfinProxyService jellyfin, IApplicationCache cache)
{
    private sealed record Candidate(string Folder, string File)
    {
        public string Script => Folder + "/" + File;
    }
    private static readonly Candidate[] Candidates = CreateCandidates();

    public async Task<PromotionNamingDetection> DetectAsync(ProtocolExecutionContext context, Guid? rootId,
        IHeaderDictionary headers, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await PromotionProfiles.RequireUserAsync(db, context, token);
        var profile = await profiles.GetMineAsync(context, token);
        var access = await libraryAccess.ResolveAsync(context, token);
        if (!access.Succeeded) throw new BackendLibraryAccessException(access);
        var approved = await roots.ReadAsync(token);
        var selected = rootId ?? profile.Values.LibraryRootId;
        var root = selected.HasValue ? approved.SingleOrDefault(item => item.Id == selected.Value) : null;
        if (root == null) return new(null, 0, 0, [], "no-folder");
        if (root.BackendLibraryId == null || !access.Allows(root.BackendLibraryId))
            throw new BackendLibraryAccessException(new(true, [], "library_index_access_denied"));
        var folder = (root.BackendPath ?? root.Path).Replace('\\', '/').TrimEnd('/') + "/";
        var identity = JsonSerializer.Serialize(new
        {
            context.Protocol,
            context.BackendInstanceId,
            context.VerifiedBackendPrincipalId,
            root,
            db.LibraryReadRevision
        });
        var key = "promotion:naming-detection:v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        if (await cache.GetAsync<PromotionNamingDetection>(key) is { } cached) return cached;
        var rows = await LibraryTrackAccess.Query(db, context, access)
            .Where(item => item.BackendLibraryId == root.BackendLibraryId && item.FilePath.Replace("\\", "/").StartsWith(folder))
            .Select(item => new { item.BackendItemId, item.FilePath, item.Title, item.Artist, item.Album, item.AlbumArtist })
            .Distinct().OrderBy(item => item.FilePath).ThenBy(item => item.BackendItemId).Take(300).ToArrayAsync(token);
        var samples = rows.DistinctBy(item => item.BackendItemId).Select(item => new
        {
            Path = Relative(folder, item.FilePath),
            Song = new Song
            {
                Id = item.BackendItemId,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album ?? "",
                AlbumArtist = item.AlbumArtist,
                Container = Path.GetExtension(item.FilePath).TrimStart('.')
            }
        }).Where(item => item.Path != null).ToArray();
        if (samples.Length == 0) return await Save(new(null, 0, 0, [], "no-songs"));
        var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var batch in samples.Select(item => item.Song.Id).Chunk(100))
            {
                var (body, status) = await jellyfin.GetJsonAsync("Items", new()
                {
                    ["Ids"] = string.Join(',', batch),
                    ["UserId"] = context.VerifiedBackendPrincipalId,
                    ["Limit"] = "100"
                }, headers, token);
                using (body)
                {
                    if (status is < 200 or >= 300 || body?.RootElement.ValueKind != JsonValueKind.Object ||
                        !body.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array)
                        return await Save(new(null, 0, samples.Length, [], "no-pattern"));
                    foreach (var item in items.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
                            metadata[id.GetString()!] = item.Clone();
                }
            }
        }
        catch (Exception error) when (error is HttpRequestException or JsonException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        { return await Save(new(null, 0, samples.Length, [], "no-pattern")); }
        foreach (var sample in samples)
            if (metadata.TryGetValue(sample.Song.Id, out var item))
            {
                sample.Song.Track = Number(item, "IndexNumber");
                sample.Song.DiscNumber = Number(item, "ParentIndexNumber");
                sample.Song.Year = Number(item, "ProductionYear");
                sample.Song.TotalDiscs = Number(item, "TotalDiscs");
            }
        foreach (var album in samples.GroupBy(item => (item.Song.AlbumArtist ?? item.Song.Artist, item.Song.Album)))
        {
            var total = album.Max(item => item.Song.DiscNumber ?? 1);
            foreach (var sample in album) sample.Song.TotalDiscs = Math.Max(sample.Song.TotalDiscs ?? 1, total);
        }
        var matches = Candidates.Select(_ => new List<string>()).ToArray();
        foreach (var sample in samples)
        {
            token.ThrowIfCancellationRequested();
            var fields = PromotionSongFields.FromSong(sample.Song);
            var rendered = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? Render(string script)
            {
                if (!rendered.TryGetValue(script, out var path)) rendered[script] = path = PromotionNaming.Render(script, fields).RelativePath;
                return path;
            }
            // Ordinary paths can discard whole folder families before evaluating their filenames.
            var longValues = fields.Values.Any(value => value != null && Encoding.UTF8.GetByteCount(value) > 160);
            for (var index = 0; index < Candidates.Length; index++)
            {
                var candidate = Candidates[index];
                if (!longValues)
                {
                    var prefix = Render(candidate.Folder) + "/";
                    if (!sample.Path!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(prefix + Render(candidate.File), sample.Path, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (string.Equals(Render(candidate.Script), sample.Path, StringComparison.OrdinalIgnoreCase)) matches[index].Add(sample.Path!);
            }
        }
        var best = Enumerable.Range(0, Candidates.Length).MaxBy(index => matches[index].Count);
        var count = matches[best].Count;
        var confident = count >= 5 && count * 5 >= samples.Length * 3;
        return await Save(new(confident ? Candidates[best].Script : null, count, samples.Length,
            confident ? matches[best].Take(3).Select(path => new PromotionNamingDetectedExample(path)).ToArray() : [], confident ? null : "no-pattern"));

        async Task<PromotionNamingDetection> Save(PromotionNamingDetection result)
        {
            await cache.SetAsync(key, result, TimeSpan.FromMinutes(5));
            return result;
        }
    }

    private static int? Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : null;

    private static string? Relative(string folder, string path)
    {
        path = path.Replace('\\', '/');
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = path[folder.Length..];
        return relative.Length == 0 || relative.Contains(':') || relative.Any(char.IsControl) ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or "..") ? null : relative;
    }

    private static Candidate[] CreateCandidates()
    {
        var result = new List<Candidate>();
        foreach (var artist in new[] { "$if2(%albumartist%,%artist%)", "%artist%" })
            foreach (var album in new[] { "%album%", "%album%$if(%year%, \\(%year%\\))", "%album%$if(%year%, [%year%])" })
                foreach (var disc in new[] { "", "$if($gt(%totaldiscs%,1),%discnumber%-)", "$if($gt(%totaldiscs%,1),Disc %discnumber%/)",
                    "$if($gt(%totaldiscs%,1),CD %discnumber%/)", "%discnumber%-", "Disc %discnumber%/", "CD %discnumber%/" })
                    foreach (var track in new[] { "$num(%tracknumber%,2)", "$num(%tracknumber%,1)" })
                        foreach (var separator in new[] { " - ", ". ", " " })
                            foreach (var title in new[] { "%title%", "%artist% - %title%" })
                                result.Add(new(artist + "/" + album, disc + track + separator + title + ".%_extension%"));
        return result.ToArray();
    }
}
