using allstarr.Core.Protocols;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Services.Local;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Matching;

public sealed record LibraryRecordingLookup(
    string PublicId, Guid? RecordingId, string? Isrc, string? ProviderId, string? ExternalId);

public sealed partial class LibraryIndexService
{
    public async Task<string?> ResolveKnownTrackAsync(ProtocolExecutionContext context, string publicId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.Actor == null) return null;
            var recording = await _identities.ResolveRecordingAsync(context.Actor, publicId, cancellationToken);
            if (recording == null) return null;
            var parsed = LocalLibraryService.ParseExternalResource(publicId);
            if (parsed.provider == "allstarr") parsed = LocalLibraryService.ParseExternalResource(recording.PublicId);
            var matches = await FindKnownRecordingTracksAsync(context,
                [new(publicId, recording.Id, recording.Isrc, parsed.provider, parsed.externalId)], cancellationToken);
            return matches.GetValueOrDefault(publicId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger?.LogWarning("Native recording lookup unavailable ({ErrorType}); retaining the external route", error.GetType().Name);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> FindKnownRecordingTracksAsync(
        ProtocolExecutionContext context, IReadOnlyList<LibraryRecordingLookup> recordings,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (context.Principal == null || recordings.Count == 0) return result;
            EnsureStorageReady();
            recordings = recordings.Select(item => item.ProviderId == null ? item : item with
            { ProviderId = _registry?.ResolveProviderId(item.ProviderId) ?? item.ProviderId }).ToArray();
            var policy = context.Policy?.UserId == context.Principal.UserId ? context.Policy :
                _effectivePolicies == null ? null :
                    await _effectivePolicies.ResolveForUserAsync(context.Principal.UserId, cancellationToken);
            var access = await _libraryAccess.ResolveAsync(context, cancellationToken);
            if (!access.Succeeded || access.LibraryIds.Length == 0) return result;
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await RequireUserAsync(db, context.Principal, cancellationToken);
            var sources = recordings.Where(item => item.ProviderId != null && item.ExternalId != null)
                .Select(item => (ProviderId: item.ProviderId!, ExternalIdHash: CanonicalCatalogKeys.Hash(item.ExternalId!))).ToArray();
            var overrides = (await ManualTrackOverrides.LoadAsync(db, context.Principal.UserId, sources, cancellationToken))
                .GroupBy(item => (item.SourceProviderId, item.SourceExternalIdHash))
                .ToDictionary(group => group.Key, group => ManualTrackOverrides.Select(group, context.Principal.UserId).Effective!);
            var pinnedIds = overrides.Values.Where(item => item.Decision == ManualOverrideDecision.Pin && item.LibraryTrackId.HasValue)
                .Select(item => item.LibraryTrackId!.Value).ToArray();
            var ids = recordings.Where(item => item.RecordingId.HasValue).Select(item => item.RecordingId!.Value).ToArray();
            var isrcs = recordings.Select(item => CleanIsrc(item.Isrc)).OfType<string>().ToArray();
            var tracks = await LibraryTrackAccess.Query(db, context, access).Where(item =>
                    pinnedIds.Contains(item.Id) || item.CanonicalRecordingId.HasValue && ids.Contains(item.CanonicalRecordingId.Value) ||
                    item.Isrc != null && isrcs.Contains(item.Isrc))
                .OrderBy(item => item.BackendLibraryId).ThenBy(item => item.BackendItemId).ThenBy(item => item.Id)
                .ToArrayAsync(cancellationToken);
            ProviderUseMode Mode(string providerId) => policy?.GetProviderMode(providerId) ??
                (_registry?.TryGet(providerId, out var descriptor) == true
                    ? descriptor!.DefaultUseMode : ProviderUseMode.Automatic);
            foreach (var recording in recordings)
            {
                if (recording.ProviderId is { } providerId && Mode(providerId) == ProviderUseMode.Off) continue;
                var manual = recording.ProviderId != null && recording.ExternalId != null
                    ? overrides.GetValueOrDefault((recording.ProviderId, CanonicalCatalogKeys.Hash(recording.ExternalId))) : null;
                LibraryTrackRecord? track;
                if (manual != null)
                {
                    // A rejection or provider pin must never be replaced by an automatic local copy.
                    track = manual.Decision == ManualOverrideDecision.Pin && manual.LibraryTrackId.HasValue
                        ? tracks.FirstOrDefault(item => item.Id == manual.LibraryTrackId) : null;
                }
                else
                {
                    if (recording.ProviderId is { } automaticProvider && Mode(automaticProvider) != ProviderUseMode.Automatic)
                        continue;
                    track = recording.RecordingId.HasValue
                        ? tracks.FirstOrDefault(item => item.CanonicalRecordingId == recording.RecordingId) : null;
                    var isrc = CleanIsrc(recording.Isrc);
                    track ??= isrc == null ? null : tracks.FirstOrDefault(item => item.Isrc == isrc &&
                        (!recording.RecordingId.HasValue || !item.CanonicalRecordingId.HasValue || item.CanonicalRecordingId == recording.RecordingId));
                }
                if (track != null) result.TryAdd(recording.PublicId, track.BackendItemId);
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger?.LogWarning("Native recording lookup unavailable ({ErrorType}); retaining external results", error.GetType().Name);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? CleanIsrc(string? value)
    {
        var normalized = value?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        return normalized?.Length == 12 ? normalized : null;
    }
}
