using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class DurationProjectionContractTests
{
    [Fact]
    public void MappingRow_UsesLocalDurationThenSourceDuration()
    {
        var retrievedAt = new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            ProviderId = "spotify",
            PayloadJson = """{"Title":"Track","Artists":["Artist"],"DurationMilliseconds":196456}""",
            RetrievedAt = retrievedAt
        };
        var method = typeof(TrackMatchesController).GetMethod(
            "Row",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var emptyLibrary = new Dictionary<Guid, LibraryTrackRecord>();
        var source = Value(method.Invoke(null,
        [
            snapshot,
            null,
            null,
            null,
            emptyLibrary,
            emptyLibrary,
            new Dictionary<Guid, ProviderTrackIdentityRecord[]>()
        ])!);
        Assert.Equal("spotify", source.GetProperty("providerId").GetString());
        Assert.Equal(196_456, source.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("spotify", source.GetProperty("durationProvenance").GetString());
        Assert.Equal(retrievedAt, source.GetProperty("durationRetrievedAt").GetDateTimeOffset());

        var localId = Guid.CreateVersion7();
        var local = new LibraryTrackRecord
        {
            Id = localId,
            Protocol = "jellyfin",
            DurationMilliseconds = 200_000,
            IndexedAt = retrievedAt.AddMinutes(1)
        };
        var decision = new TrackMatchRecord
        {
            State = TrackMatchState.Accepted,
            LibraryTrackId = localId
        };
        var target = Value(method.Invoke(null,
        [
            snapshot,
            decision,
            null,
            null,
            new Dictionary<Guid, LibraryTrackRecord> { [localId] = local },
            emptyLibrary,
            new Dictionary<Guid, ProviderTrackIdentityRecord[]>()
        ])!);
        Assert.Equal(200_000, target.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal("jellyfin", target.GetProperty("durationProvenance").GetString());
        Assert.Equal(local.IndexedAt, target.GetProperty("durationRetrievedAt").GetDateTimeOffset());
    }

    [Fact]
    public void ActivityItem_ExposesDurationMilliseconds()
    {
        var item = new AdminUiActivityItem(
            "id", "matching", "spotify", "Matched", "accepted", "detail", DateTimeOffset.UtcNow,
            DurationMilliseconds: 196_456);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            item,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal(196_456, document.RootElement.GetProperty("durationMilliseconds").GetInt64());
    }

    [Fact]
    public void MappingRow_AllowsMissingOptionalCandidateEvidence()
    {
        var candidateId = Guid.CreateVersion7();
        var row = typeof(TrackMatchesController).GetMethod(
            "Row",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
        [
            new ExternalMetadataSnapshotRecord
            {
                Id = Guid.CreateVersion7(),
                ProviderId = "spotify",
                PayloadJson = """{"Title":"Track","Artists":["Artist"]}"""
            },
            new TrackMatchRecord
            {
                State = TrackMatchState.Suggested,
                CandidateResultsJson = $$"""[{"LibraryTrackId":"{{candidateId}}","AlbumEvidence":null,"DurationDeltaMilliseconds":null}]"""
            },
            null,
            null,
            new Dictionary<Guid, LibraryTrackRecord>(),
            new Dictionary<Guid, LibraryTrackRecord>(),
            new Dictionary<Guid, ProviderTrackIdentityRecord[]>()
        ])!;

        Assert.Single(Value(row).GetProperty("candidates").EnumerateArray());
    }

    private static JsonElement Value(object row)
    {
        var value = row.GetType().GetProperty("Value")!.GetValue(row);
        return JsonSerializer.SerializeToElement(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null
        });
    }
}
