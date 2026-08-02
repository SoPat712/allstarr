using System.Diagnostics;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using Xunit.Abstractions;

namespace allstarr.Tests;

public sealed class PlaylistMaterializationPlannerTests(ITestOutputHelper output)
{
    [Fact]
    public void Reconciliation_baseline_is_linear_at_100_1000_and_10000_tracks()
    {
        var baselines = new List<(int Count, long Allocated, long ElapsedTicks)>();
        foreach (var count in new[] { 100, 1_000, 10_000 })
        {
            var entries = Enumerable.Range(0, count)
                .Select(index => Entry(index, $"source-{index}"))
                .ToArray();
            var source = Source(entries);
            var decisions = entries
                .Select((entry, index) => Accepted(entry, $"local-{index}"))
                .ToArray();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            var plan = Planner().Plan(
                PlaylistPlanMode.Reconcile, source, decisions, Target(), Rules());
            timer.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(count, plan.Entries.Count);
            Assert.Equal(count, plan.OrderedBackendItemIds.Count);
            Assert.Equal(
                Enumerable.Range(0, count).Select(index => $"local-{index}"),
                plan.OrderedBackendItemIds);
            Assert.Equal(
                plan.IdempotencyKey,
                Planner().Plan(
                    PlaylistPlanMode.Reconcile, source, decisions, Target(), Rules()).IdempotencyKey);
            baselines.Add((count, allocated, timer.ElapsedTicks));
            output.WriteLine(
                $"reconciliation tracks={count} allocated_bytes={allocated} elapsed_ticks={timer.ElapsedTicks}");
        }

        Assert.All(baselines.Zip(baselines.Skip(1)), pair =>
            Assert.True(pair.Second.Allocated < pair.First.Allocated * 30,
                $"Allocation growth from {pair.First.Count} to {pair.Second.Count} tracks was quadratic."));
    }

    [Fact]
    public void Accepted_local_matches_keep_first_source_order_and_deduplicate_backend_items()
    {
        var source = Source(Entry(0, "first"), Entry(1, "second"), Entry(2, "duplicate"), Entry(3, "third"));
        var decisions = new[]
        {
            Accepted(source.Entries[0], "local-b"),
            Accepted(source.Entries[1], "local-a"),
            Accepted(source.Entries[2], "local-b"),
            Accepted(source.Entries[3], "local-c")
        };

        var plan = Planner().Plan(PlaylistPlanMode.Reconcile, source, decisions, Target(), Rules());

        Assert.Equal(["local-b", "local-a", "local-c"], plan.OrderedBackendItemIds);
        Assert.Equal(
            [PlaylistPreviewEntryStatus.Included, PlaylistPreviewEntryStatus.Included, PlaylistPreviewEntryStatus.Duplicate, PlaylistPreviewEntryStatus.Included],
            plan.Entries.Select(entry => entry.Status));
        Assert.Equal(source.Entries[0].SourceEntryId, plan.Entries[2].DuplicateOfSourceEntryId);
        Assert.Equal([0, 1, 2], plan.Entries.Where(entry => entry.Status == PlaylistPreviewEntryStatus.Included).Select(entry => entry.TargetPosition));
    }

    [Fact]
    public void Backend_preview_keeps_source_rows_and_writes_only_native_a_and_c()
    {
        var source = Source(
            RichEntry(0, "a", "spotify-a", "Song A"),
            RichEntry(1, "b", "apple-b", "Song B"),
            RichEntry(2, "c", "spotify-c", "Song C"),
            RichEntry(3, "d", "deezer-d", "Song D"));
        var decisions = new[]
        {
            Accepted(source.Entries[0], "backend-a"),
            Decision(source.Entries[1], TrackMatchReviewState.Accepted, confidence: .95, route: new(
                TrackRouteKind.External, ProviderId: "apple", ExternalId: "apple-track-b")),
            Accepted(source.Entries[2], "backend-c"),
            Decision(source.Entries[3], TrackMatchReviewState.Unresolved)
        };

        var plan = Planner().Plan(PlaylistPlanMode.Virtual, source, decisions, Target(), Rules());

        Assert.Equal(["backend-a", "backend-c"], plan.OrderedBackendItemIds);
        Assert.Equal([true, false, true, false], plan.Entries.Select(item => item.TargetEligible));
        Assert.Equal(
            [PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem,
                PlaylistMaterializationOutcomeCodes.SkippedExternalOnlyForBackend,
                PlaylistMaterializationOutcomeCodes.IncludedNativeBackendItem,
                PlaylistMaterializationOutcomeCodes.SkippedUnresolved],
            plan.Entries.Select(item => item.OutcomeCode));
        Assert.Equal("spotify", plan.Entries[0].SourceIdentity!.ProviderId);
        Assert.Equal("Song A", plan.Entries[0].SourceMetadata!.Title);
        Assert.Equal(TrackRouteKind.External, plan.Entries[1].ResolvedRoute!.Kind);
        Assert.Equal("apple-track-b", plan.Entries[1].ResolvedRoute!.ExternalId);
        Assert.Equal([0, 1], plan.Entries.Where(item => item.TargetEligible).Select(item => item.TargetPosition));
    }

    [Fact]
    public void Low_confidence_accept_is_skipped_but_manual_pin_is_included()
    {
        var source = Source(Entry(0, "weak"), Entry(1, "pinned"));
        var decisions = new[]
        {
            Accepted(source.Entries[0], "weak-local", confidence: 0.70, threshold: 0.88),
            Decision(source.Entries[1], TrackMatchReviewState.Pinned, "pinned-local", confidence: 0.10, threshold: 0.88,
                reasons: ["manual_override_pinned"])
        };

        var plan = Planner().Plan(PlaylistPlanMode.Virtual, source, decisions, Target(), Rules());

        Assert.Equal(["pinned-local"], plan.OrderedBackendItemIds);
        Assert.Equal(PlaylistPreviewEntryStatus.BelowAcceptanceThreshold, plan.Entries[0].Status);
        Assert.Contains("accepted_match_below_persisted_threshold", plan.Entries[0].Reasons);
        Assert.Equal(PlaylistPreviewEntryStatus.Included, plan.Entries[1].Status);
        Assert.Contains("manual_override_pinned", plan.Entries[1].Reasons);
    }

    [Fact]
    public void Every_non_actionable_match_state_stays_visible_with_a_reason()
    {
        var source = Source(
            Entry(0, "missing"), Entry(1, "suggested"), Entry(2, "ambiguous"),
            Entry(3, "rejected"), Entry(4, "wrong-backend"), Entry(5, "stale"));
        var decisions = new[]
        {
            Decision(source.Entries[1], TrackMatchReviewState.Suggested),
            Decision(source.Entries[2], TrackMatchReviewState.Ambiguous),
            Decision(source.Entries[3], TrackMatchReviewState.Rejected),
            Accepted(source.Entries[4], "foreign", backendInstanceId: "backend-elsewhere"),
            Accepted(source.Entries[5], "stale") with { ExternalSnapshotId = Guid.CreateVersion7() }
        };

        var plan = Planner().Plan(PlaylistPlanMode.Virtual, source, decisions, Target(), Rules());

        Assert.Equal(
            [PlaylistPreviewEntryStatus.Unresolved, PlaylistPreviewEntryStatus.Suggested, PlaylistPreviewEntryStatus.Ambiguous,
                PlaylistPreviewEntryStatus.Rejected, PlaylistPreviewEntryStatus.WrongBackend, PlaylistPreviewEntryStatus.StaleDecision],
            plan.Entries.Select(entry => entry.Status));
        Assert.All(plan.Entries, entry => Assert.NotEmpty(entry.Reasons));
        Assert.Empty(plan.OrderedBackendItemIds);
    }

    [Fact]
    public void Snapshot_staleness_is_reported_without_changing_the_immutable_preview()
    {
        var source = Source(Entry(0, "one"));
        var decisions = new[] { Accepted(source.Entries[0], "local-1") };

        var current = Planner().Plan(
            PlaylistPlanMode.Virtual, source, decisions, Target(), Rules(), source.SnapshotId);
        var stale = Planner().Plan(
            PlaylistPlanMode.Virtual, source, decisions, Target(), Rules(), Guid.CreateVersion7());

        Assert.False(current.SourceSnapshotIsStale);
        Assert.True(stale.SourceSnapshotIsStale);
        Assert.Equal(current.OrderedBackendItemIds, stale.OrderedBackendItemIds);
        Assert.Equal(source.SourceRevision, stale.SourceRevision);
    }

    [Fact]
    public void Retry_identity_is_stable_and_later_recreate_generation_gets_a_new_identity()
    {
        var source = Source(Entry(0, "one"));
        var decisions = new[] { Accepted(source.Entries[0], "local-1") };
        var generationSeven = Rules(generation: 7);

        var first = Planner().Plan(PlaylistPlanMode.Recreate, source, decisions, Target(), generationSeven);
        var retry = Planner().Plan(PlaylistPlanMode.Recreate, source, decisions, Target(), generationSeven);
        var later = Planner().Plan(PlaylistPlanMode.Recreate, source, decisions, Target(), Rules(generation: 8));
        var differentRule = Planner().Plan(PlaylistPlanMode.Recreate, source, decisions, Target(), Rules(generation: 7, ruleVersion: "rules-2"));

        Assert.Equal(first.IdempotencyKey, retry.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, later.IdempotencyKey);
        Assert.NotEqual(first.IdempotencyKey, differentRule.IdempotencyKey);
        Assert.StartsWith("playlist-materialize:", first.IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_provider_revision_with_a_new_snapshot_identity_gets_new_materialization_work()
    {
        var first = Source(Entry(0, "one"));
        var second = new ImmutablePlaylistSourceSnapshot(
            Guid.CreateVersion7(),
            first.PlaylistLinkId,
            first.SourceRevision,
            first.Name,
            first.Entries,
            first.Description,
            first.ArtworkReference);

        var firstKey = PlaylistMaterializationPlanner.ComputeIdempotencyKey(
            first, Target(), Rules());
        var secondKey = PlaylistMaterializationPlanner.ComputeIdempotencyKey(
            second, Target(), Rules());

        Assert.NotEqual(firstKey, secondKey);
    }

    [Fact]
    public void Virtual_reconcile_and_recreate_plans_share_preview_but_only_materialized_modes_write()
    {
        var source = Source(Entry(0, "one"));
        var decisions = new[] { Accepted(source.Entries[0], "local-1") };
        var rules = Rules(preserveManual: true, mirrorStale: true, syncOwned: ["entry-owned"]);

        var virtualPlan = Planner().Plan(PlaylistPlanMode.Virtual, source, decisions, Target(), rules);
        var reconcile = Planner().Plan(PlaylistPlanMode.Reconcile, source, decisions, Target(), rules);
        var recreate = Planner().Plan(PlaylistPlanMode.Recreate, source, decisions, Target(), rules);

        Assert.False(virtualPlan.RequiresBackendWrite);
        Assert.True(reconcile.RequiresBackendWrite);
        Assert.True(recreate.RequiresBackendWrite);
        Assert.Equal(virtualPlan.OrderedBackendItemIds, reconcile.OrderedBackendItemIds);
        Assert.Equal(reconcile.OrderedBackendItemIds, recreate.OrderedBackendItemIds);
        Assert.True(reconcile.Rules.PreserveManualEntries);
        Assert.True(reconcile.Rules.MirrorStaleSyncOwnedEntries);
        Assert.Equal(["entry-owned"], reconcile.Rules.SyncOwnedMembershipIds);
        Assert.Equal("Playlist", reconcile.Metadata.Name);
        Assert.Equal("Description", reconcile.Metadata.Description);
        Assert.Equal("artwork-key", reconcile.Metadata.ArtworkReference);
    }

    [Fact]
    public void Planner_surface_has_no_http_database_download_audio_or_file_action_dependency()
    {
        var planProperties = typeof(PlaylistMaterializationPlan).GetProperties().Select(property => property.Name).ToArray();
        var methodTypes = typeof(PlaylistMaterializationPlanner).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .ToArray();

        Assert.DoesNotContain(methodTypes, type => type.Namespace?.Contains("Storage", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(planProperties, name => name.Contains("Download", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(planProperties, name => name.Contains("Audio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(planProperties, name => name.Contains("File", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(PlaylistMaterializationPlanner).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => typeof(HttpClient).IsAssignableFrom(field.FieldType));
    }

    [Fact]
    public void Generation_must_be_positive_and_artwork_must_be_a_stable_reference_key()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules(generation: 0));
        Assert.Throws<ArgumentException>(() => new ImmutablePlaylistSourceSnapshot(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "revision", "Playlist", [],
            artworkReference: "https://provider.test/art.jpg?token=secret"));

        var stable = new ImmutablePlaylistSourceSnapshot(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "revision", "Playlist", [],
            artworkReference: "provider-artwork-key-123");
        Assert.Equal("provider-artwork-key-123", stable.ArtworkReference);
    }

    private static PlaylistMaterializationPlanner Planner() => new();
    private static PlaylistPlanningTarget Target() => new("jellyfin", "backend-1", "playlist-1", "rev-1", "fingerprint-1");

    private static PlaylistPlanningRules Rules(
        long generation = 1,
        string ruleVersion = "rules-1",
        bool preserveManual = true,
        bool mirrorStale = false,
        IEnumerable<string>? syncOwned = null) =>
        new(ruleVersion, generation, preserveManual, mirrorStale, syncOwned);

    private static ImmutablePlaylistSourceSnapshot Source(params ImmutablePlaylistSourceEntry[] entries) =>
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "source-revision-1", "Playlist", entries, "Description", "artwork-key");

    private static ImmutablePlaylistSourceEntry Entry(int position, string reference) =>
        new(Guid.CreateVersion7(), position, Guid.CreateVersion7(), reference);

    private static ImmutablePlaylistSourceEntry RichEntry(int position, string reference, string externalHash, string title) =>
        new(Guid.CreateVersion7(), position, Guid.CreateVersion7(), reference,
            new("spotify", Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), externalHash, "source-revision-1", 1),
            new(title, ["Artist"], "Album", 123_000, "spotify", "USRC1", false, "https://art.test/a.jpg"));

    private static PersistedPlaylistMatchDecision Accepted(
        ImmutablePlaylistSourceEntry entry,
        string backendItemId,
        double confidence = 0.95,
        double threshold = 0.88,
        string backendInstanceId = "backend-1") =>
        Decision(entry, TrackMatchReviewState.Accepted, backendItemId, confidence, threshold, backendInstanceId);

    private static PersistedPlaylistMatchDecision Decision(
        ImmutablePlaylistSourceEntry entry,
        TrackMatchReviewState state,
        string? backendItemId = null,
        double confidence = 0.5,
        double threshold = 0.88,
        string backendInstanceId = "backend-1",
        IReadOnlyList<string>? reasons = null,
        PlaylistResolvedRoute? route = null) =>
        new(entry.SourceEntryId, entry.ExternalSnapshotId, state,
            backendItemId == null ? null : Guid.CreateVersion7(), backendItemId,
            backendItemId == null ? null : backendInstanceId,
            confidence, threshold, 1, reasons ?? [], [], Route: route);
}
