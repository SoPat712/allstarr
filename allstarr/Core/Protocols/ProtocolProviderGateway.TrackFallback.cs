using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Core.Settings;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private async Task<bool> TryMatchPlaybackAlternativeAsync(ProtocolExecutionContext protocol,
        ProviderExternalResourceId source, IReadOnlyList<string> order,
        EffectiveProviderPolicySnapshot? policy, ProviderAudioQuality quality)
    {
        if (identities == null || protocol.IsReadOnly || protocol.IsPreparation || protocol.IsOriginalDownload) return false;
        var clock = timeProvider ?? TimeProvider.System;
        var deadline = clock.GetUtcNow().Add(SearchSettings.SearchBudget);
        if (protocol.Deadline < deadline) deadline = protocol.Deadline;
        var remaining = deadline - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return false;
        using var timeout = new CancellationTokenSource(remaining, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(protocol.CancellationToken, timeout.Token);
        var matcher = (playbackMatcher ?? new TrackMatchDecisionEngine()).WithLocalPriorityWindow(
            policy?.LocalPreferenceWindow ?? 0.07, policy?.Preferences.ExplicitFilter == "CleanOnly"
                ? ProviderExplicitContentPolicy.CleanOnly : ProviderExplicitContentPolicy.Allow);
        foreach (var provider in order.Where(provider => provider != source.ProviderId && IsAutomaticProvider(provider, policy)))
        {
            protocol.CancellationToken.ThrowIfCancellationRequested();
            if (bounded.IsCancellationRequested) break;
            try
            {
                var streaming = await router.PlanAsync<IProviderStreamingCapability>(Request(protocol.RequireActor(),
                    ProviderCapabilityKind.Streaming, "playback-alternative", [provider], null, quality,
                    allowManagedDownloads: true, correlationId: protocol.CorrelationId, deadline: deadline, cancellationToken: bounded.Token));
                var authorized = streaming.Candidates.FirstOrDefault();
                if (authorized == null) continue;
                var evidence = await identities.GetRecordingAvailabilityAsync(authorized.Context, ProtocolItemId(source), bounded.Token);
                if (evidence == null || evidence.ProviderIds.ContainsKey(provider) ||
                    !Guid.TryParse(evidence.SampleId, out var recordingId) || policy?.Includes(evidence.ExplicitContentLyrics) == false) continue;
                var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(protocol.RequireActor(),
                    ProviderCapabilityKind.Metadata, "playback-alternative-match", [provider], null,
                    correlationId: protocol.CorrelationId, deadline: deadline, cancellationToken: bounded.Token));
                var metadata = plan.Candidates.FirstOrDefault();
                if (metadata == null) continue;
                var sourceSnapshot = new ExternalTrackMatchSnapshot(evidence.SampleId, source.ProviderId, source.Value,
                    evidence.Title, string.Join(", ", evidence.ArtistNames), null, null, evidence.DurationMilliseconds,
                    evidence.Isrc, null, evidence.ExplicitContentLyrics switch { 1 => true, 0 or 3 => false, _ => null }, recordingId);
                var scope = new TrackMatchScope(protocol.RequireActor().EffectiveUserId!.Value,
                    protocol.BackendInstanceId, authorized.Context.Account?.AccountId, 1, 1, new HashSet<string>());
                var tracks = new List<ProviderTrackMetadata>();
                if (!string.IsNullOrWhiteSpace(evidence.Isrc))
                {
                    var exact = await metadata.Implementation.LookupByIsrcAsync(metadata.Context, new(evidence.Isrc)).WaitAsync(bounded.Token);
                    if (exact.IsSuccess) tracks.Add(exact.RequireValue());
                    if (await AcceptAsync()) return true;
                }
                if (string.IsNullOrWhiteSpace(evidence.Title) || evidence.ArtistNames.Count == 0) continue;
                var query = $"{evidence.Title} {string.Join(' ', evidence.ArtistNames)}";
                if (query.Length > 500) query = query[..500];
                var search = await metadata.Implementation.SearchTracksAsync(metadata.Context, new(query, new(15))).WaitAsync(bounded.Token);
                if (search.IsSuccess) tracks.AddRange(search.RequireValue().Items);
                if (await AcceptAsync()) return true;

                async Task<bool> AcceptAsync()
                {
                    var songs = tracks.Where(track => track.Id.ProviderId == provider).DistinctBy(track => track.Id)
                        .Select(Map).Where(song => policy?.Includes(song) != false &&
                            !(sourceSnapshot.IsExplicit == false && song.ExplicitContentLyrics == 1)).ToArray();
                    var candidates = songs.Select(song => TrackMatchPlayableSearch.ToCandidate(song, scope)).ToArray();
                    var decision = matcher.Decide(scope, sourceSnapshot, candidates, providerPriority: order);
                    if (decision.State != TrackMatchReviewState.Accepted || decision.RequiresReview || decision.SelectedLibraryTrackId == null) return false;
                    var selected = songs.First(song => TrackMatchPlayableSearch.CandidateId(song.ExternalProvider!, song.ExternalId!) == decision.SelectedLibraryTrackId);
                    var track = new ProviderExternalResourceId(provider, ProviderResourceKind.Track, selected.ExternalId!);
                    var linked = await identities.LinkAsync(authorized.Context, new(recordingId, track,
                        authorized.Context.Account == null ? ProviderIdentityScope.Catalog : ProviderIdentityScope.Account,
                        ProviderIdentityVerification.Verified, "availability", 1), bounded.Token);
                    if (linked.Status is not (TrackIdentityLinkStatus.Created or TrackIdentityLinkStatus.AlreadyLinked)) return false;
                    await identities.RememberPlaybackMetadataAsync(authorized.Context, track, selected, bounded.Token);
                    return true;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !protocol.CancellationToken.IsCancellationRequested)
            { logger?.LogDebug("Playback match is unavailable for {Provider} ({ErrorType})", provider, error.GetType().Name); }
        }
        protocol.CancellationToken.ThrowIfCancellationRequested();
        return false;
    }
}
