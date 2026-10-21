using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace allstarr.Tests;

public sealed class ProviderUseControllerTests
{
    [Theory]
    [InlineData(null, "automatic", "automatic", true)]
    [InlineData("automatic", "automatic", "automatic", true)]
    [InlineData("automatic", "manualOnly", "automatic", true)]
    [InlineData("manualOnly", "automatic", "manualOnly", true)]
    [InlineData("automatic", "off", "off", false)]
    public async Task GetExposesBothLayersAndWhetherPersonalChoiceIsAllowed(
        string? personal, string household, string effective, bool canChoose)
    {
        var (controller, settings, policies, user) = Fixture();
        var defaults = new ListeningPreferences(ProviderModes: new Dictionary<string, string> { ["source"] = household });
        settings.Setup(item => item.GetPreferencesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(defaults, defaults, true, "household-revision"));
        settings.Setup(item => item.GetPreferencesAsync(user, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(
                new(ProviderModes: new Dictionary<string, string> { ["source"] = personal ?? household }),
                defaults, personal == null, "personal-revision")
            {
                ProviderModeOverrides = personal == null ? null : new Dictionary<string, string> { ["source"] = personal }
            });
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Policy(household));
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(Policy(effective));
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controller.Get(default)).Value);
        Assert.Equal("household-revision", json.GetProperty("householdRevision").GetString());
        Assert.Equal("personal-revision", json.GetProperty("personalRevision").GetString());
        var source = Assert.Single(json.GetProperty("providers").EnumerateArray());
        Assert.Equal("source", source.GetProperty("providerId").GetString());
        Assert.Equal(household, source.GetProperty("householdMode").GetString());
        Assert.Equal(personal, source.GetProperty("personalMode").GetString());
        Assert.Equal(effective, source.GetProperty("effectiveMode").GetString());
        Assert.Equal(canChoose, source.GetProperty("canChoosePersonal").GetBoolean());
        Assert.Equal(5, source.EnumerateObject().Count());
    }

    [Fact]
    public async Task PersonalOverrideCanBeClearedAndConflictsReturnConflict()
    {
        var (controller, settings, _, user) = Fixture();
        settings.Setup(item => item.UpdateProviderModeAsync(user, "source", null, "stale", user, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RuntimeSettingConflictException("changed"));
        Assert.IsType<ConflictObjectResult>(await controller.Update("source", new(null, "personal", "stale"), default));
        settings.VerifyAll();
    }

    [Fact]
    public async Task PersonalOverrideCanBeClearedToFollowHousehold()
    {
        var (controller, settings, policies, user) = Fixture();
        settings.Setup(item => item.UpdateProviderModeAsync(user, "source", null, "revision", user, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        settings.Setup(item => item.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new(), new(), true, "next-revision"));
        policies.Setup(item => item.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Policy("automatic"));
        policies.Setup(item => item.ResolveForUserAsync(user, It.IsAny<CancellationToken>())).ReturnsAsync(Policy("automatic"));
        var json = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await controller.Update("source", new(null, "personal", "revision"), default)).Value);
        Assert.Null(Assert.Single(json.GetProperty("providers").EnumerateArray()).GetProperty("personalMode").GetString());
        settings.Verify(item => item.UpdateProviderModeAsync(user, "source", null, "revision", user,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HouseholdChangesRequireAnAdministrator()
    {
        var (controller, _, _, _) = Fixture();
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await controller.Update("source",
            new("off", "household", "revision"), default)).StatusCode);
    }

    private static (ProviderUseController Controller, Mock<IDurableRuntimeSettings> Settings,
        Mock<IEffectiveProviderPolicyResolver> Policies, Guid User) Fixture()
    {
        var user = Guid.CreateVersion7();
        var settings = new Mock<IDurableRuntimeSettings>(MockBehavior.Strict);
        var policies = new Mock<IEffectiveProviderPolicyResolver>(MockBehavior.Strict);
        var metadata = new Mock<IProviderMetadataCapability>();
        metadata.SetupGet(item => item.ProviderId).Returns("source");
        metadata.SetupGet(item => item.Capability).Returns(ProviderCapabilityKind.Metadata);
        var registry = new ProviderRegistry([new(new("source", "Source", "Source", ProviderOrigin.BuiltIn,
            "1", "1", [new(ProviderCapabilityKind.Metadata, ProviderCapabilitySupportState.Supported,
                ProviderAccountRequirement.None, "1", ["searchTracks", "getTrack"])], new()), [metadata.Object])]);
        var controller = new ProviderUseController(settings.Object, policies.Object, registry)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[AdminAuthSessionService.HttpContextSessionItemKey] = new AdminAuthSession
        {
            SessionId = "session",
            UserId = "listener",
            UserName = "Listener",
            IsAdministrator = false,
            AllstarrUserId = user,
            JellyfinAccessToken = "fixture",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1)
        };
        return (controller, settings, policies, user);
    }

    private static EffectiveProviderPolicySnapshot Policy(string mode) => new(
        ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
        ImmutableHashSet<string>.Empty, "High", 0.07)
    { ProviderModes = ImmutableDictionary<string, ProviderUseMode>.Empty.Add("source", ProviderUsePolicy.ParseMode(mode)) };
}
