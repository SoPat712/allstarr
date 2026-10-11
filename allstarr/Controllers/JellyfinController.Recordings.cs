using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private Task<string?> ResolveKnownNativeTrackAsync(string publicId) => _libraryIndex == null
        ? Task.FromResult<string?>(null)
        : _libraryIndex.ResolveKnownTrackAsync(HttpContext.RequireProtocolExecutionContext(), publicId, HttpContext.RequestAborted);

    private async Task PreferKnownNativeSongsAsync(List<Song> external, List<Dictionary<string, object?>> native)
    {
        if (_libraryIndex == null || external.Count == 0) return;
        var context = HttpContext.RequireProtocolExecutionContext();
        var matches = await _libraryIndex.FindKnownRecordingTracksAsync(context, external.Select(song =>
            new LibraryRecordingLookup(song.Id, song.CanonicalRecordingId, song.Isrc, song.ExternalProvider, song.ExternalId)).ToArray(),
            HttpContext.RequestAborted);
        foreach (var pair in matches.GroupBy(item => item.Value))
        {
            var item = native.FirstOrDefault(item => item.GetValueOrDefault("Id")?.ToString() == pair.Key);
            if (item == null)
            {
                var (document, status) = await _proxyService.GetJsonAsync(
                    $"Users/{Uri.EscapeDataString(context.VerifiedBackendPrincipalId)}/Items/{Uri.EscapeDataString(pair.Key)}",
                    null, Request.Headers);
                using (document)
                {
                    if (status != StatusCodes.Status200OK || document == null ||
                        !document.RootElement.TryGetProperty("Id", out var id) || id.GetString() != pair.Key ||
                        !document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "Audio") continue;
                    item = document.RootElement.Deserialize<Dictionary<string, object?>>();
                }
                if (item == null) continue;
                native.Add(item);
            }
            var aliases = pair.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            external.RemoveAll(song => aliases.Contains(song.Id));
        }
    }
}
