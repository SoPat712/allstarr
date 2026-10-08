using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Playlists;

namespace allstarr.Tests;

public sealed class ProviderPlaylistUpdatePlannerTests
{
    [Fact]
    public void Ordered_diff_preserves_duplicates_without_reporting_shifted_tracks_as_moves()
    {
        var current = new[]
        {
            Track("a"),
            Track("a"),
            Track("b"),
            Track("removed")
        };
        var desired = new[]
        {
            Track("a"),
            Track("b"),
            Track("a"),
            Track("added")
        };

        var diff = ProviderPlaylistUpdateDiffPlanner.Build(current, desired);

        Assert.Equal(1, diff.DuplicateCount);
        Assert.Single(diff.Changes, item => item.Kind == "add" && item.ToPosition == 3);
        Assert.Single(diff.Changes, item => item.Kind == "remove" && item.FromPosition == 3);
        Assert.Single(diff.Changes, item => item.Kind == "move");

        var removalOnly = ProviderPlaylistUpdateDiffPlanner.Build(
            [Track("removed"), Track("a"), Track("b")],
            [Track("a"), Track("b")]);
        Assert.Single(removalOnly.Changes);
        Assert.Equal("remove", removalOnly.Changes[0].Kind);
    }

    [Fact]
    public void Mutation_contract_rejects_cross_provider_tracks_before_an_adapter_can_run()
    {
        var exception = Assert.Throws<ArgumentException>(() => new ProviderPlaylistMutationRequest(
            "spotify",
            "Road mix",
            [new ProviderExternalResourceId("apple-musickit", ProviderResourceKind.Track, "track")],
            ProviderPlaylistConflictBehavior.FailIfChanged,
            new ProviderExternalResourceId("spotify", ProviderResourceKind.Playlist, "playlist"),
            "revision"));

        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Durable_payload_contains_only_scope_and_confirmation_hashes()
    {
        var payload = new ProviderPlaylistUpdateJobPayload(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            4,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64));

        var json = JsonSerializer.Serialize(payload);

        Assert.DoesNotContain("track", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("playlistId\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("providerAccount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Playlist_controller_keeps_preview_read_only_and_apply_administrator_confirmed()
    {
        var controller = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Controllers", "PlaylistLinksController.cs"));

        Assert.Contains("[HttpGet(\"{id:guid}/source-update/preview\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"{id:guid}/source-update/apply\")]", controller, StringComparison.Ordinal);
        Assert.Contains("Only an administrator can update a source playlist.", controller, StringComparison.Ordinal);
        Assert.Contains("Only the playlist owner can update its source playlist.", controller, StringComparison.Ordinal);
        Assert.Contains("preview.ConfirmationId.Equals(request.ConfirmationId", controller, StringComparison.Ordinal);
        Assert.Contains("ProviderPlaylistUpdateJobPayload", controller, StringComparison.Ordinal);
    }

    private static ProviderPlaylistUpdateTrack Track(string id) => new(
        new ProviderExternalResourceId("spotify", ProviderResourceKind.Track, id),
        id,
        "Artist");

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
            directory = directory.Parent;
        return Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root."),
            Path.Combine(parts));
    }
}
