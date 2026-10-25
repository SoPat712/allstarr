using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Fact]
    public async Task ProblemReportAndReviewStateSurviveDatabaseAndKeyRingRestore()
    {
        var id = Guid.NewGuid();
        var request = Guid.NewGuid();
        var context = JsonSerializer.Serialize(new ProblemReportContext(1, DateTimeOffset.UtcNow, null, null,
            new("ext-fixture-song-track", null, null, null, "Fixture song", "Artist", null, null, "fixture"), null, null, []),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ProblemReports.Add(new()
            {
                Id = id,
                ActorUserId = _userId,
                RequestId = request,
                RequestFingerprint = new string('a', 64),
                Kind = "playback",
                Note = "Fixture note",
                ReporterName = "Listener",
                ContextJson = context,
                Status = "fixed",
                Revision = 3,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                StatusChangedByUserId = _userId
            });
            await db.SaveChangesAsync();
        }
        var service = Service();
        var backup = await service.CreateAsync();
        await using (var db = await _factory.CreateDbContextAsync())
            await db.ProblemReports.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "new").SetProperty(item => item.Revision, 4));
        await StageAsync(service, backup);
        await RestartAsync();
        Assert.Equal(DurableStorageReadiness.Ready, _state.GetSnapshot().Readiness);
        await using var restored = await _factory.CreateDbContextAsync();
        var report = await restored.ProblemReports.SingleAsync();
        Assert.Equal(id, report.Id);
        Assert.Equal(request, report.RequestId);
        Assert.Equal("fixed", report.Status);
        Assert.Equal(3, report.Revision);
        Assert.Equal("Fixture note", report.Note);
        Assert.Equal(context, report.ContextJson);
        Assert.Equal(_userId, report.ActorUserId);
        Assert.Equal(_userId, report.StatusChangedByUserId);
        Assert.Equal(_secretId, (await restored.SecretReferences.SingleAsync()).Id);
    }
}
