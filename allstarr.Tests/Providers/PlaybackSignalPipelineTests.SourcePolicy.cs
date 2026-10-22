using System.Collections.Immutable;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Settings;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [InlineData(ProviderUseMode.ManualOnly)]
    [InlineData(ProviderUseMode.Off)]
    public async Task ListeningEnrichmentRespectsSourcePolicyBeforeQueueingAndExecution(ProviderUseMode mode)
    {
        var policy = new EffectiveProviderPolicySnapshot(
            ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
            ImmutableHashSet<string>.Empty, "lossless", 0)
        {
            ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("musicbrainz", mode)
        };
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(policy);
        var settings = Options.Create(new MusicBrainzSettings { Enabled = false });
        var scope = new IntelligenceScope(user, "jellyfin", "backend");
        var occurrenceKey = new string('a', 64);
        var queue = new MusicBrainzListeningEnrichmentQueue(jobs, settings, policies.Object);
        Assert.False(await queue.IsEnabledForAsync(scope, default));
        Assert.Null(await queue.EnqueueAsync(scope, occurrenceKey, "restricted", default));
        Assert.Null(await queue.EnqueueImportedTrackAsync(scope, "track", occurrenceKey, "restricted", default));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.Jobs.AnyAsync(item => item.Type == MusicBrainzListeningEnrichmentQueue.JobType));

        await new MusicBrainzListeningEnrichmentQueue(jobs, settings)
            .EnqueueAsync(scope, occurrenceKey, "saved-before-policy-change", default);
        var claim = await jobs.ClaimNextAsync("listening-worker", [MusicBrainzListeningEnrichmentQueue.JobType]);
        var handler = new MusicBrainzListeningEnrichmentJobHandler(factory, null!, clock, policies.Object);
        var result = await handler.ExecuteAsync(new(claim!, EmptyServices.Instance), default);
        Assert.Equal(DurableJobCompletionKind.Succeeded, result.Kind);
    }
}
