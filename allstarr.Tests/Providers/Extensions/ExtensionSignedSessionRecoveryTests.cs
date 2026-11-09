using System.Net;
using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed partial class ExtensionAutomaticVerificationTests
{
    [Fact]
    public async Task StatusReadBeforeVersionAdoptionDoesNotDiscardTheRetainedSignIn()
    {
        await ExpiredClientAsync(expire: false);
        var updated = Client("fixture@2");
        Assert.Equal(ExtensionSessionStates.SignedOut, (await updated.ViewAsync(default)).State);

        var view = await updated.AdoptPreviousVersionAsync([SessionConfig()], default);

        Assert.NotNull(view);
        Assert.Equal(ExtensionSessionStates.SignedIn, view.State);
        Assert.True(view.Restored);
        Assert.Equal(2, Directory.GetFiles(_root, "*.protected").Length);
    }

    [Fact]
    public async Task RenewalRejectionBeforeExpiryAllowsAutomaticVerification()
    {
        var client = await ExpiredClientAsync(expire: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
        _handler.RefreshStatus = HttpStatusCode.PreconditionRequired;

        Assert.Equal("verification_required", (await client.RefreshDueAsync(default))!.LastRenewalFailure);
        Assert.Equal(_handler.OriginalExpiry, await client.AutomaticVerificationExpiryAsync(default));
        Assert.Equal(_handler.OriginalExpiry, await Client().AutomaticVerificationExpiryAsync(default));
        Assert.NotNull(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));
    }

    [Fact]
    public async Task ExpiredRestoreRequiringVerificationRetainsAutomaticEligibility()
    {
        var client = await ExpiredClientAsync();
        Assert.Equal("verification_required", (await client.RefreshDueAsync(default))!.LastRenewalFailure);
        Assert.Equal(_handler.OriginalExpiry, await client.AutomaticVerificationExpiryAsync(default));
        Assert.Equal(_handler.OriginalExpiry, await Client().AutomaticVerificationExpiryAsync(default));
    }

    [Fact]
    public async Task RefreshDuringAutomaticVerificationDoesNotLeaveAStaleManualBlock()
    {
        var client = await ExpiredClientAsync(expire: false);
        _clock.Advance(TimeSpan.FromMinutes(30));
        await client.RefreshDueAsync(default);
        Assert.NotNull(await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default));

        await client.RefreshDueAsync(default);

        Assert.Null(client.Status().AuthUrl);
        Assert.Equal(_handler.OriginalExpiry, await client.AutomaticVerificationExpiryAsync(default));
    }

    [Fact]
    public async Task PlaybackDoesNotReplaceAnUnexpiredManualLink()
    {
        var client = await ExpiredClientAsync();
        var manual = await client.StartVerificationViewAsync(default);

        client.SignedFetch("GET", "/catalog", null, null);

        Assert.Equal(1, _handler.Bootstraps);
        Assert.Equal(manual.VerificationUrl, client.Status().AuthUrl);
        Assert.Null(await client.AutomaticVerificationExpiryAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedManualLinkExpiresAndAutomaticCompletionClearsIt(bool shortLink)
    {
        var client = await ExpiredClientAsync();
        if (shortLink)
            _handler.BootstrapBody = "{\"challenge_id\":\"short-link\",\"expires_at\":\"2026-10-19T14:02:00Z\"}";
        await client.StartVerificationViewAsync(default);
        _clock.Advance(TimeSpan.FromMinutes(shortLink ? 1 : 9));
        Assert.Null(await client.AutomaticVerificationExpiryAsync(default));
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(_handler.OriginalExpiry, await client.AutomaticVerificationExpiryAsync(default));
        _handler.BootstrapBody = null;
        var challenge = (await client.PrepareAutomaticVerificationAsync(_handler.OriginalExpiry, default))!;
        Assert.True((await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default)).Restored);
        Assert.Null(client.Status().AuthUrl);
        Assert.Equal(ExtensionSessionStates.SignedIn, (await client.ViewAsync(default)).State);
    }

    [Fact]
    public async Task AbandonedFirstSignInAlsoAllowsAutomaticVerification()
    {
        var client = Client();
        await client.StartVerificationViewAsync(default);
        Assert.Null(await client.AutomaticVerificationExpiryAsync(default));
        _clock.Advance(TimeSpan.FromMinutes(10));
        var expiry = await client.AutomaticVerificationExpiryAsync(default);
        Assert.NotNull(expiry);
        var challenge = (await client.PrepareAutomaticVerificationAsync(expiry, default))!;
        Assert.True((await client.CompleteAutomaticVerificationAsync(challenge, "browser-grant", _ => Task.FromResult(true), default)).Restored);
        Assert.Null(client.Status().AuthUrl);
    }
}
