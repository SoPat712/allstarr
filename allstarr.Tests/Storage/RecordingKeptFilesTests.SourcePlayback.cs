using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin)]
    [InlineData(ProtocolKind.Subsonic)]
    public async Task AccountScopedLegacySourceUsesKeptAfterSourceAccountRetirement(ProtocolKind protocol)
    {
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            identity.Scope = ProviderIdentityScope.Account;
            identity.ProviderAccountId = account;
            await db.SaveChangesAsync();
        }

        var published = await Service().PublishAsync(Request());
        await DisableSourceAccountAsync();
        await SetListenerBackendAsync(protocol);

        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.SetupGet(item => item.IsEnabled).Returns(true);
        cache.Setup(item => item.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = KeptGateway(router.Object, cache.Object, activity);
        var context = PlaybackContext(protocol, other, "ext-deezer-song-source-track");

        var initial = await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, null);
        Assert.NotNull(initial);
        Assert.Equal("allstarr", initial.ServingProviderId);
        Assert.Equal(published.ManagedFileId, initial.ServingManagedFileId);
        Assert.Equal(audio, await initial.Response.Content.ReadAsByteArrayAsync());
        initial.Response.Dispose();

        var resumed = await gateway.OpenStreamAsync(context, "deezer", "source-track",
            ProviderAudioQuality.Any, "bytes=5-");
        Assert.NotNull(resumed);
        Assert.Equal("allstarr", resumed.ServingProviderId);
        Assert.Equal(published.ManagedFileId, resumed.ServingManagedFileId);
        var response = await ExecuteResponseAsync(resumed.Response, HttpMethods.Get, "bytes=5-");
        Assert.Equal(StatusCodes.Status206PartialContent, response.StatusCode);
        Assert.Equal(audio[5..], response.Body);

        cache.Verify(item => item.ExistsAsync(It.IsAny<string>()), Times.Never);
        router.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CurrentCanonicalIdentityWinsOverOlderPublicationSource()
    {
        await Service().PublishAsync(Request());
        var corrected = (await identities.CreateRecordingAsync(Actor(owner), "corrected-recording")).Recording.Id;
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            identity.CanonicalRecordingId = corrected;
            identity.Revision++;
            await db.SaveChangesAsync();
        }
        var source = new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, "source-track");
        Assert.Null(await Service().FindBySourceAsync(Actor(other), source));
        Assert.NotNull(await Service().FindAsync(Actor(other), recordingId));
    }

    [Fact]
    public async Task AmbiguousRetiredAccountScopedLegacySourceDoesNotChooseAKeptRecording()
    {
        await using (var db = new AllstarrDbContext(database.Options))
        {
            var identity = await db.ProviderTrackIdentities.SingleAsync();
            identity.Scope = ProviderIdentityScope.Account;
            identity.ProviderAccountId = account;
            await db.SaveChangesAsync();
        }

        await Service().PublishAsync(Request());

        var secondAccount = Guid.CreateVersion7();
        var secondJob = (await queue.EnqueueAsync(new DurableJobEnqueueRequest<object>(
            "recording.keep", "fixture-keep-other", new { item = "other-track" }, other))).JobId;
        Assert.NotNull(await queue.ClaimNextAsync("fixture-other"));
        var secondRecording = (await identities.CreateRecordingAsync(Actor(other), "fixture-recording-other")).Recording.Id;
        var secondContext = new ProviderExecutionContext(
            Actor(other), "deezer",
            new(secondAccount, "deezer", ProviderAccountScope.Personal, 0, ownerUserId: other),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
                ProviderExplicitContentPolicy.Allow, true, true, true),
            "fixture-source-other", "fixture-source-other", DateTimeOffset.UtcNow.AddMinutes(5), default);

        await using (var db = new AllstarrDbContext(database.Options))
        {
            db.ProviderAccounts.Add(new()
            {
                Id = secondAccount,
                OwnerUserId = other,
                ProviderId = "deezer",
                DisplayName = "Other source",
                Enabled = true,
                Revision = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await identities.LinkAsync(secondContext, new(secondRecording,
            new("deezer", ProviderResourceKind.Track, "source-track"), ProviderIdentityScope.Account,
            ProviderIdentityVerification.Verified, "fixture-exact", 1));

        var workspace = await artifacts.CreateWorkspaceAsync(new(
            other, secondJob, "deezer", secondAccount, "fixture-source-other"));
        await using var input = new MemoryStream(audio);
        var written = await artifacts.WriteAsync(new(
            workspace.Reference, secondJob, "deezer", "source-other.flac", input, 1000));
        var secondArtifact = await artifacts.ResolveAsync(workspace.Reference,
            new(written.ArtifactId, written.Sha256, written.SizeBytes, new("audio/flac", "flac", "flac"), true),
            sourceTrack: new("deezer", ProviderResourceKind.Track, "source-track"), sourceAccountRevision: 0);
        var secondRequest = new KeptRecordingPublication(
            secondRecording, other, secondJob, secondArtifact.Id, "deezer",
            $"keep:fixture:{other:N}:{secondRecording:N}", new("Fixture other", "Artist", Extension: ".flac"))
        { AttemptNumber = 1 };
        await Service().PublishAsync(secondRequest);

        await using (var db = new AllstarrDbContext(database.Options))
        {
            var accounts = await db.ProviderAccounts
                .Where(item => item.Id == account || item.Id == secondAccount)
                .ToArrayAsync();
            foreach (var saved in accounts)
            {
                saved.Enabled = false;
                saved.Revision++;
            }
            await db.SaveChangesAsync();
        }

        var cache = new Mock<IApplicationCache>(MockBehavior.Strict);
        cache.SetupGet(item => item.IsEnabled).Returns(true);
        cache.Setup(item => item.ExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var gateway = KeptGateway(router.Object, cache.Object);
        var stream = await gateway.OpenStreamAsync(
            PlaybackContext(ProtocolKind.Jellyfin, other, "ext-deezer-song-source-track"),
            "deezer", "source-track", ProviderAudioQuality.Any, null);

        Assert.Null(stream);
        cache.Verify(item => item.ExistsAsync(It.IsAny<string>()), Times.Once);
        router.VerifyNoOtherCalls();
    }
}
