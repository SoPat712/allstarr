using allstarr.Core.Capabilities;

namespace allstarr.Tests;

public sealed class ProviderCapabilityContractTests
{
    [Fact]
    public void EverySdkV1CapabilityInterface_ReceivesTheTypedExecutionContext()
    {
        Type[] capabilityInterfaces =
        [
            typeof(IProviderMetadataCapability),
            typeof(IProviderStreamingCapability),
            typeof(IProviderDownloadCapability),
            typeof(IProviderPlaylistCapability),
            typeof(IProviderLyricsCapability),
            typeof(IProviderIntelligenceCapability),
            typeof(IProviderHealthProbeCapability)
        ];

        Assert.Equal(capabilityInterfaces.Length, Enum.GetValues<ProviderCapabilityKind>().Length);
        foreach (var capabilityInterface in capabilityInterfaces)
        {
            Assert.True(typeof(IProviderCapability).IsAssignableFrom(capabilityInterface));
            Assert.All(
                capabilityInterface.GetMethods().Where(method => !method.IsSpecialName),
                method => Assert.Equal(
                    typeof(ProviderExecutionContext),
                    method.GetParameters()[0].ParameterType));
        }
    }

    [Fact]
    public void MetadataLookups_RejectAResourceKindBeforeCallingTheProvider()
    {
        var track = TrackId("deezer", "track-17");
        var album = new ProviderExternalResourceId(
            "deezer",
            ProviderResourceKind.Album,
            "album-17");

        Assert.Equal(track, new ProviderTrackLookupRequest(track).Id);
        Assert.Equal(album, new ProviderAlbumLookupRequest(album).Id);
        Assert.Throws<ArgumentException>(() => new ProviderTrackLookupRequest(album));
        Assert.Throws<ArgumentException>(() => new ProviderAlbumLookupRequest(track));
    }

    [Fact]
    public void StreamLease_ExpressesExpiryRangeSeekMediaAndRefreshBehavior()
    {
        var media = Media();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var lease = new ProviderStreamLease(
            "lease-17",
            new Uri("https://media.example.invalid/signed-source"),
            expiresAt,
            supportsByteRanges: true,
            supportsSeeking: true,
            media,
            ProviderStreamRetryBehavior.RefreshLease,
            qualityDowngradeReason: "The account tier limits this track to lossy audio.");

        Assert.Equal(expiresAt, lease.ExpiresAt);
        Assert.True(lease.SupportsByteRanges);
        Assert.True(lease.SupportsSeeking);
        Assert.Equal("flac", lease.Media.Codec);
        Assert.Equal(ProviderStreamRetryBehavior.RefreshLease, lease.RetryBehavior);
        Assert.Equal("The account tier limits this track to lossy audio.", lease.QualityDowngradeReason);
        Assert.DoesNotContain("signed-source", lease.ToString(), StringComparison.Ordinal);
        Assert.Null(typeof(ProviderStreamLease).GetProperty("SourceUri"));
        Assert.DoesNotContain(
            "signed-source",
            System.Text.Json.JsonSerializer.Serialize(lease),
            StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ProviderStreamLease(
            "unsafe",
            new Uri("https://media.example.invalid/source"),
            expiresAt,
            true,
            true,
            media,
            ProviderStreamRetryBehavior.DoNotRetry,
            qualityDowngradeReason: "token=must-not-be-reported"));
    }

    [Fact]
    public void DownloadContract_UsesDurableJobManagedWorkspaceProgressAndVerifiedArtifact()
    {
        var track = TrackId("qobuz", "123");
        var jobId = Guid.CreateVersion7();
        var request = new ProviderDownloadRequest(
            track,
            jobId,
            new ProviderManagedWorkspaceReference("workspace-17"),
            ProviderAudioQuality.Lossless);
        var progress = new ProviderDownloadProgress(
            ProviderDownloadProgressStage.Transferring,
            bytesCompleted: 50,
            totalBytes: 100);
        var artifact = new ProviderDownloadedArtifact(
            "artifact-17",
            new string('a', 64),
            100,
            Media(),
            verified: true);

        Assert.Equal(jobId, request.DurableJobId);
        Assert.Equal("workspace-17", request.Workspace.WorkspaceId);
        Assert.Equal(50, progress.BytesCompleted);
        Assert.True(artifact.Verified);
        Assert.DoesNotContain(
            request.GetType().GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new ProviderDownloadedArtifact(
            "artifact-17",
            new string('a', 64),
            100,
            Media(),
            verified: false));
    }

    [Fact]
    public void PlaylistContract_PreservesOwnerRevisionMetadataPagingAndOrder()
    {
        var playlistId = new ProviderExternalResourceId(
            "spotify",
            ProviderResourceKind.Playlist,
            "playlist-17");
        var summary = new ProviderPlaylistSummary(
            playlistId,
            "Road trip",
            new ProviderPlaylistOwner("owner-17"),
            sourceRevision: "snapshot-3",
            description: "A playlist",
            artwork: new ProviderArtworkReference(publicUri: new Uri("https://images.example.invalid/art.jpg")),
            trackCount: 2);
        var page = new ProviderPlaylistTrackPage(
            summary,
            new ProviderPage<ProviderPlaylistTrack>(
                "spotify",
                [
                    new ProviderPlaylistTrack(0, TrackId("spotify", "track-1")),
                    new ProviderPlaylistTrack(1, TrackId("spotify", "track-2"))
                ],
                nextCursor: "opaque-cursor",
                snapshotVersion: "snapshot-3"));

        Assert.Equal([0, 1], page.Tracks.Items.Select(item => item.Position));
        Assert.Equal("snapshot-3", page.Playlist.SourceRevision);
        Assert.Equal("opaque-cursor", page.Tracks.NextCursor);
        Assert.Throws<ArgumentException>(() => new ProviderPlaylistTrackPage(
            summary,
            new ProviderPage<ProviderPlaylistTrack>(
                "spotify",
                [
                    new ProviderPlaylistTrack(1, TrackId("spotify", "track-1")),
                    new ProviderPlaylistTrack(0, TrackId("spotify", "track-2"))
                ])));
        Assert.Throws<ArgumentException>(() => new ProviderPlaylistTrackPage(
            summary,
            new ProviderPage<ProviderPlaylistTrack>(
                "spotify",
                [new ProviderPlaylistTrack(0, TrackId("deezer", "track-1"))])));
    }

    [Fact]
    public void AlbumMetadata_RejectsCrossProviderArtistAndTrackIds()
    {
        var albumId = new ProviderExternalResourceId(
            "deezer",
            ProviderResourceKind.Album,
            "album-17");
        var foreignArtist = new ProviderArtistCredit(
            "Artist",
            new ProviderExternalResourceId(
                "qobuz",
                ProviderResourceKind.Artist,
                "artist-17"));

        Assert.Throws<ArgumentException>(() => new ProviderAlbumMetadata(
            albumId,
            "Album",
            [foreignArtist]));
        Assert.Throws<ArgumentException>(() => new ProviderAlbumMetadata(
            albumId,
            "Album",
            [new ProviderArtistCredit("Artist")],
            tracks:
            [
                new ProviderTrackMetadata(
                    TrackId("qobuz", "track-1"),
                    "Track",
                    [new ProviderArtistCredit("Artist")])
            ]));
    }

    [Fact]
    public void TrackMetadata_PreservesPositiveBitrateAndRejectsInvalidValues()
    {
        var track = new ProviderTrackMetadata(
            TrackId("deezer", "track-1"),
            "Track",
            [new ProviderArtistCredit("Artist")],
            bitrate: 320_000);

        Assert.Equal(320_000, track.Bitrate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderTrackMetadata(
            TrackId("deezer", "track-1"),
            "Track",
            [new ProviderArtistCredit("Artist")],
            bitrate: 0));
    }

    [Fact]
    public void PlaylistMutationIntent_PreservesRecreateChoiceAndRejectsCrossProviderIds()
    {
        var request = new ProviderPlaylistMutationRequest(
            "spotify",
            "Road trip",
            [TrackId("spotify", "track-1"), TrackId("spotify", "track-2")],
            ProviderPlaylistConflictBehavior.Recreate,
            description: "A playlist");

        Assert.Equal(ProviderPlaylistConflictBehavior.Recreate, request.ConflictBehavior);
        Assert.Equal(["track-1", "track-2"], request.OrderedTrackIds.Select(item => item.Value));
        Assert.Throws<ArgumentException>(() => new ProviderPlaylistMutationRequest(
            "spotify",
            "Road trip",
            [TrackId("deezer", "track-1")],
            ProviderPlaylistConflictBehavior.Reconcile));
    }

    [Fact]
    public void PlaylistMutationReceipt_IsCompactSafeAndProviderOwned()
    {
        var receipt = new ProviderPlaylistMutationReceipt(
            new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist-1"),
            "revision-2",
            2,
            applied: true,
            ["Playlist artwork was not changed."]);

        Assert.True(receipt.Applied);
        Assert.Equal("revision-2", receipt.Revision);
        Assert.Equal(2, receipt.TrackCount);
        Assert.Equal("Playlist artwork was not changed.", Assert.Single(receipt.Warnings));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderPlaylistMutationReceipt(
            receipt.PlaylistId,
            null,
            -1,
            applied: false));
    }

    [Fact]
    public void LyricsContract_SeparatesAvailabilityFromContentAndUsesCanonicalIdentity()
    {
        var recordingId = Guid.CreateVersion7();
        var request = new ProviderLyricsRequest(
            recordingId,
            TrackId("spotify", "track-17"),
            availabilityOnly: true,
            ProviderLyricsFormat.LineTimed);
        var availability = new ProviderLyricsResult(
            ProviderLyricsAvailabilityState.Available,
            "lyricsplus");
        var document = new ProviderLyricsResult(
            ProviderLyricsAvailabilityState.Available,
            "lyricsplus",
            ProviderLyricsFormat.LineTimed,
            "[00:01.00]First line\n[00:02.00]Second line\n");

        Assert.Equal(recordingId, request.CanonicalRecordingId);
        Assert.Null(availability.Content);
        Assert.EndsWith("\n", document.Content, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ProviderLyricsResult(
            ProviderLyricsAvailabilityState.Unavailable,
            "lyricsplus",
            ProviderLyricsFormat.PlainText,
            "content"));
    }

    [Fact]
    public void HealthProbe_IsCapabilitySpecificAndBoundedByExecutionContextContract()
    {
        var request = new ProviderHealthProbeRequest(
            ProviderCapabilityKind.Download,
            nonDestructive: true);
        var result = new ProviderHealthProbeResult(
            ProviderProbeStatus.Degraded,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(12),
            "rate-limited");

        Assert.Equal(ProviderCapabilityKind.Download, request.TargetCapability);
        Assert.True(request.NonDestructive);
        Assert.Equal(ProviderProbeStatus.Degraded, result.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ProviderHealthProbeRequest(ProviderCapabilityKind.Health));
    }

    private static ProviderExternalResourceId TrackId(string providerId, string value) =>
        new(providerId, ProviderResourceKind.Track, value);

    private static ProviderMediaFormat Media() => new(
        "audio/flac",
        "flac",
        "flac",
        bitrate: 1_000_000,
        sampleRate: 48_000,
        bitDepth: 24,
        channels: 2);
}
