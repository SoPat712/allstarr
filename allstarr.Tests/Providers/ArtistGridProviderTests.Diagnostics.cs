using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Providers.ArtistGrid;
using allstarr.Core.Routing;
using allstarr.Models.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using allstarr.Services.Common;
using Moq;

namespace allstarr.Tests.Providers;

public sealed partial class ArtistGridProviderTests
{
    [Fact]
    public async Task MeasurementUsesTheWaveHeaderRatherThanTheSourceExtension()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        using var http = SourceClient("https://files.example/song.flac", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(allstarr.Tests.AudioMediaFactsTests.Wave()) };
            response.Content.Headers.ContentType = new("audio/wav");
            return response;
        });
        var catalog = new ArtistGridCatalog(http);
        var registry = new ProviderRegistry([ArtistGridProviderRegistration.CreateRegistration(new(catalog), new(catalog, http))]);
        using var selector = new ProviderCtsTrackSelector(new ProviderCheckServiceTests.Factory(database.Options));
        var runner = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector, Mock.Of<IProviderOutcomeObserver>());
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin"));
        var result = await runner.MeasureAsync(actor, "artistgrid", null, ProviderAudioQuality.Any, "wave-check", householdCheck: true);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("wav", result.Media!.Container);
        Assert.Equal("pcm_s24le", result.Media.Codec);
        Assert.Equal(24, result.Media.BitDepth);
        Assert.Equal(44100, result.Media.SampleRate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingHealth_UsesAnIndexedSongAndReadsTheSourceWithoutAnAccount(bool available)
    {
        var mediaCalls = 0;
        using var http = SourceClient("https://files.example/song.mp3", request =>
        {
            mediaCalls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("bytes=0-0", request.Headers.Range?.ToString());
            var response = Response("a", null, "audio/mpeg");
            response.StatusCode = available ? HttpStatusCode.PartialContent : HttpStatusCode.ServiceUnavailable;
            if (available) response.Content.Headers.ContentRange = new(0, 0, 1000);
            return response;
        });
        var catalog = new ArtistGridCatalog(http);
        var registration = ArtistGridProviderRegistration.CreateRegistration(new(catalog), new(catalog, http));
        var registry = new ProviderRegistry([registration]);
        Assert.True(registration.Descriptor.HealthProbe);
        Assert.Equal(ProviderUseMode.ManualOnly, registration.Descriptor.DefaultUseMode);
        var result = await new ProviderTryOperations(registry).ProbeAsync(Context(), registration.Descriptor, ProviderCapabilityKind.Streaming);
        Assert.Equal(available, result.Succeeded);
        Assert.Equal(1, mediaCalls);
    }

    [Fact]
    public async Task Measurement_FindsAnIndexedSongOnAFreshManualSource()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        using var http = SourceClient("https://files.example/song.mp3", request =>
        {
            var response = Response("audio", null, "audio/mpeg");
            Assert.Equal(HttpMethod.Get, request.Method);
            return response;
        });
        var catalog = new ArtistGridCatalog(http);
        var registry = new ProviderRegistry([ArtistGridProviderRegistration.CreateRegistration(new(catalog), new(catalog, http))]);
        using var selector = new ProviderCtsTrackSelector(new ProviderCheckServiceTests.Factory(database.Options));
        var runner = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector, Mock.Of<IProviderOutcomeObserver>());
        var actor = new ProviderActorContext(ProviderActorKind.Administrator, Guid.NewGuid(), new("jellyfin", "fixture", "admin"));
        var result = await runner.MeasureAsync(actor, "artistgrid", null, ProviderAudioQuality.HighResolution, "manual-source", householdCheck: true);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(5, result.SampleBytes);
        Assert.Equal("mp3", result.Media!.Codec);

        var factory = new ProviderCheckServiceTests.Factory(database.Options);
        await using (var db = factory.CreateDbContext())
        {
            db.Users.Add(new()
            {
                Id = actor.UserId!.Value,
                Enabled = true,
                IsAdmin = true,
                DisplayName = "Fixture",
                BackendType = "jellyfin",
                BackendInstanceId = "fixture",
                BackendPrincipalId = "admin"
            });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ProviderChecks:PauseMilliseconds"] = "0" }).Build();
        using var services = new ServiceCollection().AddSingleton<IProviderRegistry>(registry)
            .AddSingleton(Mock.Of<IProviderRouteAccountResolver>()).AddSingleton<ProviderDiagnosticExecutionFactory>()
            .AddSingleton<ProviderTryOperations>().BuildServiceProvider();
        var status = new ProviderStatusManager(config, Mock.Of<IHttpClientFactory>(), NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()), Options.Create(new AppleDownloadSettings()),
            Options.Create(new DeezerSettings()), Options.Create(new QobuzSettings()), services: services);
        var history = new ProviderCheckHistory(factory, TimeProvider.System);
        var measured = new ProviderCtsDiagnosticRunner(registry, Mock.Of<IProviderRouteAccountResolver>(), selector,
            Mock.Of<IProviderOutcomeObserver>(), factory);
        var sweep = new ProviderAccountCheckRunner(factory, status, history, config, TimeProvider.System,
            NullLogger<ProviderAccountCheckRunner>.Instance, registry: registry, media: measured);
        Assert.True(await sweep.SweepAsync(default, "artistgrid"));
        var checks = await history.ReadLatestAsync(default);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, check => Assert.True(check.Status.IsReady));
        Assert.Contains(checks, check => check.Key.Capability == "streaming");
        await using var ledger = factory.CreateDbContext();
        Assert.Single(ledger.AuditEvents.Where(item => item.Category == "provider-cts" && item.Action == "scheduled.measure"));
    }
}
