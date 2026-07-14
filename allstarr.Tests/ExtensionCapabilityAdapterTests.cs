using allstarr.Core.Capabilities;
using allstarr.Core.Extensions;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ExtensionCapabilityAdapterTests
{
    [Fact]
    public async Task Metadata_AllDeclaredAlbumAndArtistHooksMapTypedSchemas()
    {
        string[] hooks = ["searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum", "searchArtists", "getArtist"];
        var manifest = Manifest(ProviderCapabilityKind.Metadata, hooks);
        var sandbox = Sandbox(manifest, """
            const artist = { id: 'artist-1', name: 'Artist', artworkUrl: 'https://img.example/artist.jpg', snapshotVersion: 'a1' };
            const album = { id: 'album-1', title: 'Album', artists: [{ id: 'artist-1', name: 'Artist' }], trackCount: 8, snapshotVersion: 'b1' };
            const track = { id: 'track-1', title: 'Track', artists: [{ id: 'artist-1', name: 'Artist' }], albumId: 'album-1', albumTitle: 'Album', durationMs: 1234, isrc: 'USABC1234567' };
            registerExtension({
              searchTracks: function() { return { items: [track] }; }, getTrack: function() { return track; }, lookupByIsrc: function() { return track; },
              searchAlbums: function() { return { items: [album], nextCursor: 'next' }; }, getAlbum: function() { return album; },
              searchArtists: function() { return { items: [artist] }; }, getArtist: function() { return artist; }
            });
            """);
        var adapter = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var search = new ProviderMetadataSearchRequest("fixture", new ProviderPageRequest(10));

        Assert.Equal("album-1", Assert.Single((await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("next", (await adapter.SearchAlbumsAsync(Context(), search)).RequireValue().NextCursor);
        Assert.Equal(8, (await adapter.GetAlbumAsync(Context(), new ProviderAlbumLookupRequest(Id(ProviderResourceKind.Album, "album-1")))).RequireValue().TrackCount);
        Assert.Equal("artist-1", Assert.Single((await adapter.SearchArtistsAsync(Context(), search)).RequireValue().Items).Id.Value);
        Assert.Equal("Artist", (await adapter.GetArtistAsync(Context(), new ProviderArtistLookupRequest(Id(ProviderResourceKind.Artist, "artist-1")))).RequireValue().Name);
        Assert.Equal("USABC1234567", (await adapter.LookupByIsrcAsync(Context(), new ProviderIsrcLookupRequest("USABC1234567"))).RequireValue().Isrc);
    }

    [Fact]
    public async Task SharedSandbox_SerializesMetadataAndHealthCapabilityCalls()
    {
        var manifest = new ExtensionSdkManifest("fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [
            new ExtensionSdkCapability(ProviderCapabilityKind.Metadata, ["searchArtists"], [ProviderAccountScope.User]),
            new ExtensionSdkCapability(ProviderCapabilityKind.Health, ["probeMetadata"], [ProviderAccountScope.User])
        ], []);
        var sandbox = Sandbox(manifest, """
            let active = false;
            function enter(value) { if (active) throw new Error('concurrent engine access'); active = true; for (let i = 0; i < 25000; i++) {} active = false; return value; }
            registerExtension({
              searchArtists: function() { return enter({ items: [{ id: 'artist-1', name: 'Artist' }] }); },
              probeMetadata: function() { return enter({ status: 'Healthy', observedAt: '2030-01-01T00:00:00Z', latencyMs: 1 }); }
            });
            """);
        var metadata = new ExtensionMetadataCapabilityAdapter(sandbox, manifest);
        var health = new ExtensionHealthCapabilityAdapter(sandbox, manifest);

        var calls = Enumerable.Range(0, 20).SelectMany(_ => new Task<bool>[]
        {
            Task.Run(async () => (await metadata.SearchArtistsAsync(Context(), new ProviderMetadataSearchRequest("x", new ProviderPageRequest(1)))).IsSuccess),
            Task.Run(async () => (await health.ProbeAsync(Context(), new ProviderHealthProbeRequest(ProviderCapabilityKind.Metadata))).IsSuccess)
        });

        Assert.All(await Task.WhenAll(calls), Assert.True);
    }

    [Fact]
    public async Task Streaming_MapsOnlyTypedLeaseAndRejectsFilesystemSource()
    {
        var manifest = Manifest(ProviderCapabilityKind.Streaming, "getStreamLease");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getStreamLease: function(request) { return {
              leaseId: 'lease-1', sourceUri: 'file:///etc/passwd', expiresAt: '2030-01-01T00:00:00Z',
              supportsByteRanges: true, supportsSeeking: true,
              media: { mimeType: 'audio/flac', container: 'flac', codec: 'flac' }, retryBehavior: 'RefreshLease'
            }; }});
            """);
        var adapter = new ExtensionStreamingCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetStreamLeaseAsync(Context(),
            new ProviderStreamLeaseRequest(Id(ProviderResourceKind.Track, "track-1")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
    }

    [Fact]
    public async Task Lyrics_RejectsOversizedProviderContentWithoutEchoingIt()
    {
        var manifest = Manifest(ProviderCapabilityKind.Lyrics, "fetchLyrics");
        var sandbox = Sandbox(manifest, """
            registerExtension({ fetchLyrics: function(request) { return {
              availability: 'Available', source: 'fixture', format: 'PlainText', content: 'x'.repeat(2000001)
            }; }});
            """);
        var adapter = new ExtensionLyricsCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.FetchLyricsAsync(Context(), new ProviderLyricsRequest(
            Guid.CreateVersion7(), Id(ProviderResourceKind.Track, "track-1")));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
        Assert.DoesNotContain("x", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playlist_ReadRequiresSelectedAccountBeforeInvokingExtension()
    {
        var manifest = Manifest(ProviderCapabilityKind.Playlist, "getUserPlaylists");
        var sandbox = Sandbox(manifest, """
            registerExtension({ getUserPlaylists: function(request) { throw new Error('secret provider diagnostic'); } });
            """);
        var adapter = new ExtensionPlaylistCapabilityAdapter(sandbox, manifest);

        var outcome = await adapter.GetUserPlaylistsAsync(Context(),
            new ProviderUserPlaylistsRequest(new ProviderPageRequest()));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, outcome.Error!.Kind);
        Assert.DoesNotContain("diagnostic", outcome.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignResourceId_IsRejectedBeforeHookInvocation()
    {
        var manifest = Manifest(ProviderCapabilityKind.Download, "checkAvailability");
        var sandbox = Sandbox(manifest, "registerExtension({ checkAvailability: function() { return { state: 'Available' }; } });");
        var adapter = new ExtensionDownloadCapabilityAdapter(sandbox, manifest);

        await Assert.ThrowsAsync<ArgumentException>(() => adapter.CheckAvailabilityAsync(Context(),
            new ProviderDownloadAvailabilityRequest(new ProviderExternalResourceId("other-provider", ProviderResourceKind.Track, "track-1"))));
    }

    private static ExtensionSdkManifest Manifest(ProviderCapabilityKind kind, params string[] hooks) => new(
        "fixture-extension", "Fixture", "1.0.0", "1", "index.js",
        [new ExtensionSdkCapability(kind, hooks, [ProviderAccountScope.User])], []);

    private static ExtensionSandbox Sandbox(ExtensionSdkManifest manifest, string script) => new(
        Path.GetTempPath(), """{"id":"fixture-extension","displayName":"Fixture","version":"1.0.0"}""", script,
        new HttpClientFactory(), NullLogger.Instance);

    private static ProviderExternalResourceId Id(ProviderResourceKind kind, string value) =>
        new("fixture-extension", kind, value);

    private static ProviderExecutionContext Context()
    {
        var actor = new ProviderActorContext(Guid.CreateVersion7(), ProviderActorKind.User, Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "user"));
        return new ProviderExecutionContext(actor, "fixture-extension", null, null,
            new ProviderExecutionPolicy(new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, false, true, ["fixture-extension"]),
            "extension-test", "extension-test-correlation", DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None,
            "extension-test-idempotency");
    }

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
