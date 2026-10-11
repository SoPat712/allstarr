using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Local;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    public async Task RememberTrackAsync(ProtocolExecutionContext protocol, string itemId)
    {
        if (identities == null) return;
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (!parsed.isExternal || parsed.type != "song") return;
        try
        {
            var provider = registry.ResolveProviderId(NormalizeProvider(parsed.provider!));
            var track = new ProviderExternalResourceId(provider, ProviderResourceKind.Track, parsed.externalId!);
            var routed = await PlanExactAsync<IProviderMetadataCapability>(protocol, provider,
                ProviderCapabilityKind.Metadata, "remember-track", track);
            if (routed.Candidate is { } candidate)
                await RememberPlayedTrackAsync(candidate.Context, candidate.TrackId ?? track, itemId);
        }
        catch (Exception error)
        {
            logger?.LogWarning("Could not retain the track identity ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task RememberPlayedTrackAsync(ProviderExecutionContext context,
        ProviderExternalResourceId track, string requestedItemId)
    {
        if (identities == null) return;
        try
        {
            await identities.RememberSourceAsync(context, track, requestedItemId,
                context.Account != null && !IsPublicMetadataProvider(context.ProviderId)
                    ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
                cancellationToken: context.CancellationToken);
        }
        catch (Exception error)
        {
            // Recording bookkeeping cannot turn an already opened audio response into a failure.
            logger?.LogWarning("Could not retain the playback identity ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task<Song?> GetKnownSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        if (identities == null) return null;
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var recording = await identities.ResolveRecordingAsync(actor, $"ext-{providerId}-song-{externalId}",
            protocol.CancellationToken);
        if (recording == null) return null;
        var policy = await ResolveEffectivePolicyAsync(protocol);
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(protocol, actor,
            ProviderCapabilityKind.Metadata, "protocol-recording-metadata",
            ResolveProviderOrder(ProviderCapabilityKind.Metadata, policy),
            new ProviderExternalResourceId("allstarr", ProviderResourceKind.Track, recording.Id.ToString("D"))));
        foreach (var candidate in plan.Candidates)
        {
            if (candidate.TrackId == null) continue;
            var outcome = await candidate.Implementation.GetTrackAsync(candidate.Context, new(candidate.TrackId));
            if (!outcome.IsSuccess)
            {
                if (outcome.Error!.Kind == ProviderErrorKind.NotFound) continue;
                ThrowRouteFailure(outcome.Error);
            }
            var song = await EnrichRelationshipsAsync(candidate, outcome.RequireValue());
            song.Id = recording.PublicId;
            song.CanonicalRecordingId = recording.Id;
            return policy?.Includes(song) == false ? null : song;
        }
        return null;
    }

    private async Task ApplyKnownPublicIdsAsync(ProviderExecutionContext context, IReadOnlyList<Song> songs)
    {
        if (identities == null || songs.Count == 0) return;
        var lookups = songs.Select(song => new TrackIdentityLookup(context,
            new ProviderExternalResourceId(context.ProviderId, ProviderResourceKind.Track, song.ExternalId!))).ToArray();
        var resolved = await identities.ResolveManyAsync(lookups, context.CancellationToken);
        for (var index = 0; index < songs.Count; index++)
        {
            var identity = resolved[index];
            if (identity?.Verification == ProviderIdentityVerification.Verified &&
                identity.VerificationMethod is not ("automatic-suggestion" or
                    ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                    ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod) &&
                !string.IsNullOrWhiteSpace(identity.PublicId))
            {
                songs[index].Id = identity.PublicId;
                songs[index].CanonicalRecordingId = identity.CanonicalRecordingId;
            }
        }
    }
}
