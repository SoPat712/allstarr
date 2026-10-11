using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Models.Domain;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
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
                songs[index].Id = identity.PublicId;
        }
    }
}
