using System.Reflection;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Matching;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class ManualAuthorityProjectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappingRow_ExposesBothLayersWithViewerPermissionsAndIndependentRevisions(bool administrator)
    {
        var viewer = Guid.CreateVersion7();
        var snapshot = new ExternalMetadataSnapshotRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = viewer,
            ProviderId = "spotify",
            BackendInstanceId = "backend",
            PayloadJson = "{}"
        };
        var personal = new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = viewer,
            ExternalSnapshotId = Guid.CreateVersion7(),
            Decision = ManualOverrideDecision.Reject,
            Reason = "Wrong track",
            Revision = 2,
            MatcherVersion = TrackMatchDecisionEngine.AlgorithmVersion
        };
        var household = new ManualTrackOverrideRecord
        {
            Id = Guid.CreateVersion7(),
            ExternalSnapshotId = Guid.CreateVersion7(),
            Decision = ManualOverrideDecision.Pin,
            TargetProviderId = "deezer",
            TargetExternalId = "target",
            Revision = 4,
            Reason = "Household choice"
        };
        var row = Row(snapshot, new(personal, household), viewer, administrator);
        Assert.Equal("personal", row.GetProperty("effectiveAuthorityScope").GetString());
        Assert.Equal(administrator ? 2 : 1, row.GetProperty("allowedAuthorityScopes").GetArrayLength());
        var authorities = row.GetProperty("manualAuthorities").EnumerateArray().ToArray();
        Assert.Equal(2, authorities.Length);
        Assert.Equal("personal", authorities[0].GetProperty("scope").GetString());
        Assert.True(authorities[0].GetProperty("canEdit").GetBoolean());
        Assert.True(authorities[0].GetProperty("effective").GetBoolean());
        Assert.Equal(2, authorities[0].GetProperty("revision").GetInt64());
        Assert.Equal("household", authorities[1].GetProperty("scope").GetString());
        Assert.Equal(administrator, authorities[1].GetProperty("canEdit").GetBoolean());
        Assert.False(authorities[1].GetProperty("effective").GetBoolean());
        Assert.Equal(4, authorities[1].GetProperty("revision").GetInt64());
        Assert.Equal("provider_match", authorities[1].GetProperty("kind").GetString());
        Assert.All(authorities, item => Assert.Equal(snapshot.Id, item.GetProperty("authoritySnapshotId").GetGuid()));
        var revealed = Row(snapshot, new(null, household), viewer, administrator);
        Assert.Equal("household", revealed.GetProperty("effectiveAuthorityScope").GetString());
        Assert.Equal("pinned", revealed.GetProperty("state").GetString());
    }

    private static JsonElement Row(ExternalMetadataSnapshotRecord snapshot, ManualTrackOverrideLayers layers,
        Guid viewer, bool administrator)
    {
        var row = typeof(TrackMatchesController).GetMethod("Row", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [snapshot, null, layers, null, new Dictionary<Guid, LibraryTrackRecord>(),
                new Dictionary<Guid, ProviderTrackIdentityRecord[]>(), new[] { "deezer" }, viewer, administrator])!;
        return JsonSerializer.SerializeToElement(row.GetType().GetProperty("Value")!.GetValue(row),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
