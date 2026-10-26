using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task AutomaticRouteRepairRemovesAnUnqualifiedCoverAndRestoresTheQualifiedRelease()
    {
        var (recording, _) = await SeedCoverRouteEvidenceAsync();

        await new CatalogIdentityMaintenance(_factory, _clock).RunAsync(default);

        var resolved = await _service.ResolveTargetAsync(Context(Actor(_userA), "deezer"), recording, new("deezer"));
        Assert.Equal("79590714", resolved?.ExternalId.Value);
        Assert.Null(await _service.ResolveRecordingAsync(Actor(_userA), "ext-deezer-song-80564534"));
        var coverLookup = Assert.Single(await _service.ResolveManyAsync(
            [new(Context(Actor(_userA), "deezer"), Track("deezer", "80564534"))]));
        Assert.Equal("automatic-suggestion", coverLookup?.VerificationMethod);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("automatic-suggestion", (await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "80564534")).VerificationMethod);
        Assert.Equal("automatic-match", (await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "79590714")).VerificationMethod);
        var before = await db.ProviderTrackIdentities.OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Revision, item.UpdatedAt, item.VerificationMethod }).ToArrayAsync();
        await new CatalogIdentityMaintenance(_factory, _clock).RunAsync(default);
        await using var restarted = await _factory.CreateDbContextAsync();
        Assert.Equal(before, await restarted.ProviderTrackIdentities.OrderBy(item => item.Id)
            .Select(item => new { item.Id, item.Revision, item.UpdatedAt, item.VerificationMethod }).ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticRouteRepairPreservesPinnedAndExplicitlyChosenCovers(bool pinnedIdentity)
    {
        var (recording, snapshot) = await SeedCoverRouteEvidenceAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var cover = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "80564534");
            if (pinnedIdentity) cover.Verification = ProviderIdentityVerification.Pinned;
            else db.ManualTrackOverrides.Add(new ManualTrackOverrideRecord
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                ExternalSnapshotId = snapshot,
                SourceProviderId = "spotify",
                SourceExternalIdHash = new string('a', 64),
                TargetProviderId = "deezer",
                TargetExternalId = "80564534",
                Decision = ManualOverrideDecision.Pin,
                Reason = "Selected this rendition",
                DecisionVersion = 1,
                MatcherVersion = "fixture",
                CreatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await new CatalogIdentityMaintenance(_factory, _clock).RunAsync(default);

        Assert.Equal("80564534", (await _service.ResolveTargetAsync(
            Context(Actor(_userA), "deezer"), recording, new("deezer")))?.ExternalId.Value);
        await using var verify = await _factory.CreateDbContextAsync();
        var retained = await verify.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "80564534");
        Assert.Equal("automatic-match", retained.VerificationMethod);
        Assert.Equal(0, retained.Revision);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"ProviderTrackIds\":{\"deezer\":\"80564534\"},\"Confidence\":0}]")]
    public async Task AutomaticRouteRepairPreservesLinksWithoutUsableStoredEvidence(string evidence)
    {
        var (recording, _) = await SeedCoverRouteEvidenceAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.TrackMatches.SingleAsync()).CandidateResultsJson = evidence;
            await db.SaveChangesAsync();
        }

        await new CatalogIdentityMaintenance(_factory, _clock).RunAsync(default);

        Assert.Equal("80564534", (await _service.ResolveTargetAsync(
            Context(Actor(_userA), "deezer"), recording, new("deezer")))?.ExternalId.Value);
    }

    private async Task<(Guid RecordingId, Guid SnapshotId)> SeedCoverRouteEvidenceAsync()
    {
        var actor = Actor(_userA);
        var recording = (await _service.CreateRecordingAsync(actor, "cover-route-fixture")).Recording.Id;
        foreach (var externalId in new[] { "79590714", "80564534" })
            await _service.LinkAsync(Context(actor, "deezer"), new(recording, Track("deezer", externalId),
                ProviderIdentityScope.Catalog, ProviderIdentityVerification.Verified, "automatic-match", 2));
        var snapshotId = Guid.CreateVersion7();
        var source = new ExternalTrackMatchSnapshot("source", "spotify", "source", "Boom Clap", "Charli xcx",
            "SUCKER", null, 169_866, null, null, false);
        LocalTrackMatchCandidate Candidate(string externalId, string artist, string isrc) => new(
            Guid.CreateVersion7(), _userA, "fixture", null, externalId, null, "Boom Clap", artist,
            "Boom Clap", null, 169_000, isrc, null, false,
            new Dictionary<string, string> { ["deezer"] = externalId }, IsLocal: false, ExternalProvider: "deezer");
        var scores = new TrackMatchDecisionEngine().ScoreCandidates(source,
            [Candidate("79590714", "Charli xcx", "GBAHS1400160"), Candidate("80564534", "Jazzy K", "USA2P1408144")]);
        await using var db = await _factory.CreateDbContextAsync();
        var correct = await db.ProviderTrackIdentities.SingleAsync(item => item.ExternalId == "79590714");
        correct.VerificationMethod = "superseded";
        correct.Revision++;
        db.ExternalMetadataSnapshots.Add(new ExternalMetadataSnapshotRecord
        {
            Id = snapshotId,
            OwnerUserId = _userA,
            BackendInstanceId = "fixture",
            BackendPrincipalId = "a",
            Protocol = "jellyfin",
            ProviderId = "spotify",
            ResourceKind = "track",
            ExternalIdHash = new string('a', 64),
            SnapshotVersion = 1,
            ProviderRevision = "fixture",
            CorrelationId = "cover-route-fixture",
            PayloadJson = JsonSerializer.Serialize(new { source.Title, Artists = new[] { source.Artist }, source.Album, source.DurationMilliseconds }),
            PayloadSha256 = new string('b', 64),
            RetrievedAt = _clock.UtcNow
        });
        db.TrackMatches.Add(new TrackMatchRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = _userA,
            ExternalSnapshotId = snapshotId,
            CanonicalRecordingId = recording,
            State = TrackMatchState.Accepted,
            Confidence = .9909,
            Threshold = .88,
            DecisionVersion = 2,
            SourceSnapshotVersion = 1,
            MatcherVersion = "accepted-routing-v20",
            PolicyVersion = "playlist-policy-v1",
            CandidateResultsJson = JsonSerializer.Serialize(scores),
            CorrelationId = "cover-route-fixture",
            DecidedAt = _clock.UtcNow
        });
        db.AuditEvents.Add(new AuditEventRecord
        {
            Id = Guid.CreateVersion7(),
            Category = "catalog-identity-maintenance",
            Action = "duplicates.complete",
            Outcome = "completed",
            CorrelationId = "fixture",
            CreatedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
        return (recording, snapshotId);
    }
}
