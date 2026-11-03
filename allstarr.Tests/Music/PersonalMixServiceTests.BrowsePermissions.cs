using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Theory]
    [InlineData("synthetic")]
    [InlineData("disabled")]
    [InlineData("missing")]
    public async Task MixBrowse_IneligibleListenersSeeNoMixesWhileWritesRemainDenied(string state)
    {
        publish = true;
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == owner);
            if (state == "synthetic") user.IsSynthetic = true;
            else if (state == "disabled") user.Enabled = false;
            else db.Users.Remove(user);
            await db.SaveChangesAsync();
        }
        Assert.Empty(await service.ListAsync(owner));
        Assert.Empty(await service.ListAsync(Context(owner)));
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        Assert.Empty(await service.PublishedAsync(Context(owner), default, Guid.NewGuid()));
        Assert.Empty(await service.PublishedAsync(Context(owner), default, summariesOnly: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GenerateAsync(owner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(owner, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("instance")]
    [InlineData("principal")]
    public async Task PublishedMixBrowse_WrongScopeReturnsEmpty(string scope)
    {
        publish = true;
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.SingleAsync(item => item.Id == owner);
            if (scope == "protocol") user.BackendType = "jellyfin";
            else if (scope == "instance") user.BackendInstanceId = "elsewhere";
            else user.BackendPrincipalId = "someone-else";
            await db.SaveChangesAsync();
        }
        Assert.Empty(await service.ListAsync(Context(owner)));
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        Assert.Empty(await service.PublishedAsync(Context(owner), default, summariesOnly: true));
    }
}
