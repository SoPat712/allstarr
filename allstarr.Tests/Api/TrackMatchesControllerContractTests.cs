using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;
using allstarr.Core.Matching;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;

namespace allstarr.Tests;

public sealed class TrackMatchesControllerContractTests
{
    [Theory]
    [InlineData("IsExplicit", "true", true)]
    [InlineData("isExplicit", "false", false)]
    [InlineData("IsExplicit", "null", null)]
    [InlineData("IsExplicit", "\"true\"", null)]
    [InlineData("OtherExplicit", "true", null)]
    public void ReviewSourceProjectionPreservesOnlyKnownExplicitBoolean(string field, string raw, bool? expected)
    {
        var source = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.NewGuid(),
            ProviderId = "spotify",
            BackendInstanceId = "fixture",
            PayloadJson = $$"""{"Title":"Power Trip (feat. Miguel)","Artists":["J. Cole","Miguel"],"{{field}}":{{raw}}}"""
        };
        var row = typeof(TrackMatchesController).GetMethod("Row", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [source, null, new ManualTrackOverrideLayers(null, null), null, new Dictionary<Guid, LibraryTrackRecord>(),
                new Dictionary<Guid, ProviderTrackIdentityRecord[]>(), Array.Empty<string>(), Guid.NewGuid(), false, null, null])!;
        var value = JsonSerializer.SerializeToElement(row.GetType().GetProperty("Value")!.GetValue(row));
        var actual = value.GetProperty("isExplicit");
        if (expected.HasValue) Assert.Equal(expected.Value, actual.GetBoolean());
        else Assert.Equal(JsonValueKind.Null, actual.ValueKind);
    }

    [Theory]
    [InlineData(" FLAC ", "flac")]
    [InlineData("pcm_s16le", "pcm_s16le")]
    [InlineData("mp4a.40.2", "mp4a.40.2")]
    [InlineData("format-name", "format-name")]
    [InlineData("abcdefghijklmnopqrstuvwxyz123456", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("abcdefghijklmnopqrstuvwxyz1234567", null)]
    [InlineData("-flac", null)]
    [InlineData("flac/pcm", null)]
    [InlineData("flac\nsecret", null)]
    [InlineData("flác", null)]
    [InlineData("UNKNOWN", null)]
    public void ReportedMediaLabelsAreBoundedAsciiTokens(string raw, string? expected)
    {
        var project = typeof(TrackMatchesController).GetMethod("ReportedMedia", BindingFlags.Static | BindingFlags.NonPublic)!;
        var value = project.Invoke(null, [new Song { Codec = raw, Container = raw }]);
        if (expected == null) Assert.Null(value);
        else
        {
            var media = JsonSerializer.SerializeToElement(value);
            Assert.Equal(expected, media.GetProperty("codec").GetString());
            Assert.Equal(expected, media.GetProperty("container").GetString());
        }
    }

    [Theory]
    [InlineData(" MP3 ")]
    [InlineData("AAC")]
    [InlineData("opus")]
    [InlineData("vorbis")]
    public void ReportedLossyMediaNeverClaimsBitDepth(string codec)
    {
        var project = typeof(TrackMatchesController).GetMethod("ReportedMedia", BindingFlags.Static | BindingFlags.NonPublic)!;
        var media = JsonSerializer.SerializeToElement(project.Invoke(null, [new Song { Codec = codec, BitDepth = 24 }]));
        Assert.Equal(JsonValueKind.Null, media.GetProperty("bitDepth").ValueKind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReviewCandidateProjectionPreservesBestVersionAndEvidence(bool best)
    {
        var score = new TrackMatchCandidateScore(Guid.CreateVersion7(), "version", .85,
            best ? ["ArtistGrid best version +5"] : [], [], Title: "Song",
            ProviderTrackIds: new Dictionary<string, string> { ["artistgrid"] = "version" },
            IsLocal: false, IsBestVersion: best);
        var parse = typeof(TrackMatchesController).GetMethod("ParseCandidates", BindingFlags.Static | BindingFlags.NonPublic)!;
        var projected = JsonSerializer.SerializeToElement(parse.Invoke(null,
            [JsonSerializer.Serialize(new[] { score }), new HashSet<string>(["artistgrid"]), new HashSet<Guid>()]));
        var candidate = Assert.Single(projected.EnumerateArray());
        Assert.Equal(best, candidate.GetProperty("isBestVersion").GetBoolean());
        Assert.Equal(best, candidate.GetProperty("reasons").EnumerateArray()
            .Any(reason => reason.GetString() == "ArtistGrid best version +5"));
    }

    [Fact]
    public void ProviderSearchUsesTheNormalSearchWithoutASourcePicker()
    {
        var method = typeof(TrackMatchesController).GetMethod(nameof(TrackMatchesController.SearchProviderTargets))!;
        Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name == "provider");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Review_row_keeps_current_match_even_without_credible_suggestions(bool provider, bool savedEvidence)
    {
        var localId = Guid.NewGuid();
        var canonicalId = Guid.NewGuid();
        var local = new LibraryTrackRecord { Id = localId, BackendInstanceId = "server", BackendItemId = "local-song", Title = "Silk & Cologne", Artist = "Ei8ht" };
        var identity = new ProviderTrackIdentityRecord
        {
            Id = Guid.NewGuid(),
            CanonicalRecordingId = canonicalId,
            ProviderId = "qobuz",
            ExternalId = "target",
            ResourceKind = ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Catalog,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "automatic-match"
        };
        var evidence = JsonSerializer.Serialize(new[] { new TrackMatchCandidateScore(localId, "local-song", .76,
            ["title_fuzzy"], ["review_needed"], new Dictionary<string, double> { ["title"] = .7, ["artist"] = .6 },
            Title: "Silk & Cologne", Artist: "Ei8ht", IsLocal: !provider,
            ProviderTrackIds: provider ? new Dictionary<string, string> { ["deezer"] = "alternate", ["qobuz"] = "target" } : null) });
        var row = typeof(TrackMatchesController).GetMethod("Row", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new ExternalMetadataSnapshotRecord { Id = Guid.NewGuid(), ProviderId = "spotify", BackendInstanceId = "server", PayloadJson = """{"title":"Silk & Cologne","artist":"Ei8ht"}""" },
                new TrackMatchRecord { State = TrackMatchState.Suggested, LibraryTrackId = provider ? null : localId,
                    CanonicalRecordingId = canonicalId, Confidence = .76, CandidateResultsJson = savedEvidence ? evidence : "[]",
                    ReasonsJson = """["title_fuzzy"]""", WarningsJson = """["review_needed"]""" },
                new ManualTrackOverrideLayers(null, null), new ProviderTrackIdentityRecord
                { Id = Guid.NewGuid(), CanonicalRecordingId = canonicalId, ProviderId = "spotify", ExternalId = "source", ResourceKind = ProviderResourceKind.Track },
                new Dictionary<Guid, LibraryTrackRecord> { [localId] = local },
                new Dictionary<Guid, ProviderTrackIdentityRecord[]> { [canonicalId] = [identity] },
                new[] { "qobuz", "deezer" }, Guid.NewGuid(), false, null, null])!;
        var value = JsonSerializer.SerializeToElement(row.GetType().GetProperty("Value")!.GetValue(row));
        var candidate = Assert.Single(value.GetProperty("candidates").EnumerateArray());
        Assert.True(candidate.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(.76, candidate.GetProperty("confidence").GetDouble());
        Assert.Contains("title_fuzzy", candidate.GetProperty("reasons").ToString());
        Assert.Contains("review_needed", candidate.GetProperty("warnings").ToString());
        Assert.Equal("Silk & Cologne", value.GetProperty("searchQuery").GetString());
        if (savedEvidence) Assert.Equal(.6, candidate.GetProperty("components").GetProperty("artist").GetDouble());
        if (provider) Assert.Equal("qobuz", candidate.GetProperty("providerTrackIds").EnumerateObject().First().Name);
    }

    [Fact]
    public void Candidate_projection_keeps_all_credible_playable_results()
    {
        var localId = Guid.NewGuid();
        var candidates =
            $$"""
            [
              { "libraryTrackId": "weak", "providerTrackIds": { "qobuz": "weak" }, "title": "Wrong", "components": { "title": 1, "artist": 0.15 } },
              { "libraryTrackId": "{{localId}}", "title": "Local" },
              { "libraryTrackId": "legacy-external", "providerTrackIds": { "qobuz": "external" }, "title": "External" },
              { "libraryTrackId": "external-2", "providerTrackIds": { "qobuz": "external-2" }, "title": "External 2" },
              { "libraryTrackId": "external-3", "providerTrackIds": { "qobuz": "external-3" }, "title": "External 3" },
              { "libraryTrackId": "external-4", "providerTrackIds": { "qobuz": "external-4" }, "title": "External 4" },
              { "libraryTrackId": "external-5", "providerTrackIds": { "qobuz": "external-5" }, "title": "External 5" },
              { "libraryTrackId": "legacy-metadata", "providerTrackIds": { "musicbrainzalbum": "release" }, "title": "MusicBrainz album" },
              { "providerTrackIds": {}, "title": "Metadata only" }
            ]
            """;
        var parse = typeof(TrackMatchesController).GetMethod(
            "ParseCandidates",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var projected = JsonSerializer.Serialize(parse.Invoke(
            null,
            [candidates, new HashSet<string>(["qobuz"]), new HashSet<Guid>([localId])]));

        Assert.Contains("Local", projected);
        Assert.Contains("External", projected);
        Assert.Contains("\"isLocal\":true", projected);
        Assert.Contains("\"isLocal\":false", projected);
        Assert.DoesNotContain("MusicBrainz album", projected);
        Assert.DoesNotContain("Metadata only", projected);
        Assert.DoesNotContain("Wrong", projected);
        Assert.Equal(6, JsonDocument.Parse(projected).RootElement.GetArrayLength());
    }

    [Fact]
    public void Provider_search_candidates_are_not_scored_as_local()
    {
        var convert = typeof(TrackMatchesController).GetMethod(
            "ToCandidate",
            BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(Song), typeof(Guid)])!;
        var candidate = convert.Invoke(null, [
            new Song { Id = "ext-qobuz-track", ExternalProvider = "qobuz", ExternalId = "track" },
            Guid.NewGuid()
        ])!;

        Assert.False((bool)candidate.GetType().GetProperty("IsLocal")!.GetValue(candidate)!);
    }

    [Fact]
    public void Attention_filter_is_disjoint_from_matched_and_unresolved()
    {
        var matches = typeof(TrackMatchesController).GetMethod(
            "MatchesStateFilter",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Filter(TrackMatchState state, string value) =>
            (bool)matches.Invoke(null, [state, value])!;

        Assert.True(Filter(TrackMatchState.Suggested, "attention"));
        Assert.True(Filter(TrackMatchState.Ambiguous, "attention"));
        Assert.False(Filter(TrackMatchState.Rejected, "attention"));
        Assert.False(Filter(TrackMatchState.Unresolved, "attention"));
        Assert.False(Filter(TrackMatchState.Accepted, "attention"));
        Assert.True(Filter(TrackMatchState.Unresolved, "unresolved"));
        Assert.True(Filter(TrackMatchState.Accepted, "matched"));
        Assert.True(Filter(TrackMatchState.Pinned, "history"));
        Assert.True(Filter(TrackMatchState.Rejected, "history"));
    }

    [Fact]
    public void Automatic_filter_excludes_every_manual_authority_and_pinned_state()
    {
        var matches = typeof(TrackMatchesController).GetMethod(
            "IsAutomaticDecision",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Filter(TrackMatchState state, bool manualMatch = false, bool manualRejection = false) =>
            (bool)matches.Invoke(null, [state, manualMatch, manualRejection])!;

        Assert.True(Filter(TrackMatchState.Accepted));
        Assert.False(Filter(TrackMatchState.Accepted, manualMatch: true));
        Assert.False(Filter(TrackMatchState.Accepted, manualRejection: true));
        Assert.False(Filter(TrackMatchState.Pinned));
        Assert.False(Filter(TrackMatchState.Suggested));
    }
}
