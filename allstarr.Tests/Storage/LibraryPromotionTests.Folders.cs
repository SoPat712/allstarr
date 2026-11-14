using allstarr.Core.Artwork;
using allstarr.Core.Caching;
using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Controllers;
using allstarr.Core.Enrichment;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using allstarr.Models.Settings;
using allstarr.Services.Admin;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task PromotionFolders_DiscoverOnlyMusicAndDeleteTheWritableProbe()
    {
        var path = Path.Combine(directory, "discovered-music");
        Directory.CreateDirectory(path);
        var missing = Path.Combine(directory, "missing-music");
        var handler = new FolderResponseHandler(JsonSerializer.Serialize(new object[]
        {
            new { Name = "Movies", CollectionType = "movies", Locations = new[] { directory } },
            new { Name = "Music", CollectionType = "music", Locations = new[] { path, missing, missing + "/" },
                LibraryOptions = new { PathInfos = new[] { new { Path = path } } } }
        }));
        using var client = new HttpClient(handler);
        var configured = FolderRoots([]);
        var result = await FolderDiscovery(configured, client).ReadAsync(FolderSession(true));

        Assert.True(result.Available);
        Assert.Equal(2, result.Folders.Length);
        var reachable = Assert.Single(result.Folders, folder => folder.BackendPath == path);
        Assert.True(reachable.Reachable);
        Assert.True(reachable.Writable);
        Assert.Equal("writable", reachable.Status);
        Assert.True(reachable.FreeBytes > 0);
        Assert.False(Assert.Single(result.Folders, folder => folder.BackendPath == missing).Writable);
        var saved = Assert.Single(await configured.ReadAsync());
        Assert.Equal("Music", saved.Name);
        Assert.Equal(path, saved.Path);
        Assert.Equal(path, saved.BackendPath);
        Assert.Equal(saved.Id, reachable.RootId);
        Assert.True((await configured.SetupAsync()).Ready);
        await FolderDiscovery(configured, client).ReadAsync(FolderSession(true));
        Assert.Equal(saved, Assert.Single(await configured.ReadAsync()));
        var full = await FolderDiscovery(FolderRoots([], minimumFree: long.MaxValue.ToString()), client).ReadAsync(FolderSession(true));
        Assert.Equal("almost-full", Assert.Single(full.Folders, folder => folder.BackendPath == path).Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.Equal("/Library/VirtualFolders", handler.Endpoint);
        Assert.Contains("fixture-folder-token", handler.Authorization);
    }

    [Fact]
    public async Task PromotionFolders_ApproveVerifiedBackendMappingWithoutAnAllowedBase()
    {
        await SetPromotionRoleAsync(true, false);
        const string source = "/jellyfin/music";
        var path = Path.Combine(directory, "mapped-music");
        Directory.CreateDirectory(path);
        var configured = FolderRoots([]);
        using var client = new HttpClient(new FolderResponseHandler(MusicFolderJson(source)));
        var discovery = FolderDiscovery(configured, client);
        var controller = FolderController(configured, discovery, true);
        var root = new PromotionRoot(Guid.NewGuid(), "Mapped music", path, source, "music");

        Assert.IsType<OkObjectResult>(await controller.SaveRoots([root with { BackendLibraryId = "unverified-library" }], default));
        Assert.Equal(root, Assert.Single(await configured.ReadAsync()));
        Assert.Equal(root, await configured.RequireAsync(root.Id, default));
        var setup = await configured.SetupAsync();
        Assert.True(setup.Ready);
        Assert.False(setup.AllowedBaseConfigured);
        var folder = Assert.Single((await discovery.ReadAsync(FolderSession(true))).Folders);
        Assert.Equal(path, folder.Path);
        Assert.True(folder.Mapped);
        Assert.True(folder.Writable);
        Assert.Equal(root.Id, folder.RootId);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    [Fact]
    public async Task PromotionFolders_FollowALinkedMusicFolderToWhereItLeads()
    {
        await SetPromotionRoleAsync(true, false);
        var real = Path.Combine(directory, ".linked-target");
        var link = Path.Combine(directory, "temp", "linked-music");
        var broken = Path.Combine(directory, "temp", "broken-music");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, real);
        Directory.CreateSymbolicLink(broken, Path.Combine(directory, "gone"));
        var configured = FolderRoots([]);
        using var client = new HttpClient(new FolderResponseHandler(JsonSerializer.Serialize(new[]
            { new { ItemId = "music", Name = "Music", CollectionType = "music", Locations = new[] { link, broken } } })));
        var discovery = FolderDiscovery(configured, client);

        var folders = (await discovery.ReadAsync(FolderSession(true))).Folders;
        var linked = Assert.Single(folders, folder => folder.BackendPath == link);
        Assert.Equal(real, linked.Path);
        Assert.True(linked.Linked);
        Assert.True(linked.Mapped);
        Assert.Equal("writable", linked.Status);
        Assert.Equal("unreachable", Assert.Single(folders, folder => folder.BackendPath == broken).Status);
        var automatic = Assert.Single(await configured.ReadAsync());
        Assert.Equal(real, automatic.Path);
        Assert.Equal(link, automatic.BackendPath);
        Assert.Equal("music", automatic.BackendLibraryId);
        Assert.Equal(automatic.Id, linked.RootId);

        var root = new PromotionRoot(Guid.NewGuid(), linked.Name, linked.Path, linked.BackendPath, "music");
        Assert.IsType<OkObjectResult>(await FolderController(configured, discovery, true).SaveRoots([root], default));
        Assert.Equal(root, await configured.RequireAsync(root.Id, default));
        var approved = Assert.Single((await discovery.ReadAsync(FolderSession(true))).Folders, folder => folder.BackendPath == link);
        Assert.Equal(root.Id, approved.RootId);
        Assert.True(approved.Linked);
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public async Task PromotionFolders_RejectUnverifiedSourcesAndArbitraryNewPaths()
    {
        await SetPromotionRoleAsync(true, false);
        var path = Path.Combine(directory, "unapproved-music");
        Directory.CreateDirectory(path);
        var configured = FolderRoots([]);
        using var client = new HttpClient(new FolderResponseHandler(MusicFolderJson("/jellyfin/music")));
        var controller = FolderController(configured, FolderDiscovery(configured, client), true);
        var forged = new PromotionRoot(Guid.NewGuid(), "Unverified", path, "/jellyfin/unverified");

        Assert.IsType<BadRequestObjectResult>(await controller.SaveRoots([forged], default));
        await Assert.ThrowsAsync<ArgumentException>(() => configured.SaveAsync(owner, [forged], default,
            new HashSet<string>(StringComparer.Ordinal) { "/jellyfin/music" }));
        await Assert.ThrowsAsync<ArgumentException>(() => configured.SaveAsync(owner,
            [new PromotionRoot(Guid.NewGuid(), "Arbitrary", path)]));
        Assert.Empty(await configured.ReadAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
    }

    [Fact]
    public async Task PromotionFolders_OptionalBaseRejectsExcludedPathsAndPreservesDirectApprovalInsideIt()
    {
        await SetPromotionRoleAsync(true, false);
        var allowed = Path.Combine(directory, "restricted");
        var inside = Path.Combine(allowed, "music");
        var outside = Path.Combine(directory, "excluded-music");
        Directory.CreateDirectory(inside);
        Directory.CreateDirectory(outside);
        var configured = FolderRoots([], allowed);
        using var client = new HttpClient(new FolderResponseHandler(MusicFolderJson(outside)));
        var result = await FolderDiscovery(configured, client).ReadAsync(FolderSession(true));
        Assert.Equal("outside-allowed-base", Assert.Single(result.Folders).Status);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => configured.SaveAsync(owner,
            [new PromotionRoot(Guid.NewGuid(), "Excluded", outside, outside)], default,
            new HashSet<string>(StringComparer.Ordinal) { outside }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        var direct = new PromotionRoot(Guid.NewGuid(), "Direct", inside);
        await configured.SaveAsync(owner, [direct]);
        Assert.Equal(direct, Assert.Single(await configured.ReadAsync()));
        Assert.True((await configured.SetupAsync()).Ready);
        Assert.True((await configured.SetupAsync()).AllowedBaseConfigured);
        Assert.Empty(Directory.EnumerateFileSystemEntries(inside));
    }

    [Fact]
    public async Task PromotionFolders_RetainApprovedRootsAfterRemovingTheOptionalBase()
    {
        await SetPromotionRoleAsync(true, false);
        var path = Path.Combine(directory, "retained-music");
        var replacement = Path.Combine(directory, "replacement-music");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(replacement);
        var approved = new PromotionRoot(Guid.NewGuid(), "Retained", path);
        var configured = FolderRoots([approved]);

        Assert.Equal(approved, await configured.RequireAsync(approved.Id, default));
        Assert.True((await configured.SetupAsync()).Ready);
        await configured.SaveAsync(owner, [approved]);
        await Assert.ThrowsAsync<ArgumentException>(() => configured.SaveAsync(owner, [approved with { Path = replacement }]));
        Assert.Equal(approved, Assert.Single(await configured.ReadAsync()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(replacement));
    }

    [Fact]
    public async Task PromotionFolders_NoReachableMusicFolderLeavesSetupNotSet()
    {
        var configured = FolderRoots([]);
        using var client = new HttpClient(new FolderResponseHandler(MusicFolderJson(Path.Combine(directory, "missing-music"))));
        var result = await FolderDiscovery(configured, client).ReadAsync(FolderSession(true));
        Assert.Equal("no-reachable-music-folder", result.Reason);
        Assert.Equal("unreachable", Assert.Single(result.Folders).Status);
        var setup = await configured.SetupAsync();
        Assert.False(setup.Ready);
        Assert.False(setup.AllowedBaseConfigured);
        Assert.Equal("library-folder-not-set", setup.Reason);
    }

    [Fact]
    public async Task PromotionFolders_OnlyAdministratorsCanDiscoverOrSaveAndOnlyTheyReceivePaths()
    {
        await SetPromotionRoleAsync(false, false);
        var root = new PromotionRoot(rootId, "Private path", Path.Combine(directory, "library", "music"));
        var configured = FolderRoots([root]);
        var handler = new FolderResponseHandler(MusicFolderJson(root.Path));
        using var client = new HttpClient(handler);
        var discovery = FolderDiscovery(configured, client);
        var controller = FolderController(configured, discovery, false);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Folders(default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.SaveRoots([root], default)).StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => discovery.ReadAsync(FolderSession(false)));
        Assert.Equal(0, handler.Calls);
        var listenerRoots = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await controller.Roots(default)).Value);
        Assert.DoesNotContain(root.Path, listenerRoots);

        await SetPromotionRoleAsync(true, false);
        var administratorRoots = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(
            await FolderController(configured, discovery, true).Roots(default)).Value);
        Assert.Contains(root.Path, administratorRoots);
    }

    [Fact]
    public async Task PromotionFolders_AutomaticLinkingPreservesManualRootsAndRejectsUnwritablePaths()
    {
        var allowed = Path.Combine(directory, "allowed");
        var path = Path.Combine(allowed, "automatic");
        var manualPath = Path.Combine(allowed, "manual");
        var denied = Path.Combine(directory, "read-only");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(manualPath);
        Directory.CreateDirectory(denied);
        await File.WriteAllTextAsync(Path.Combine(denied, "unchanged.txt"), "keep");
        var manual = new PromotionRoot(Guid.NewGuid(), "My chosen name", manualPath, manualPath, "music");
        var configured = FolderRoots([manual], allowed);
        using var client = new HttpClient(new FolderResponseHandler(JsonSerializer.Serialize(new[]
            { new { ItemId = "music", Name = "Discovered name", CollectionType = "music", Locations = new[] { path, manualPath, denied } } })));
        var discovery = FolderDiscovery(configured, client);
        await Task.WhenAll(discovery.ReadAsync(FolderSession(true)), discovery.ReadAsync(FolderSession(true)));
        var saved = await configured.ReadAsync();
        Assert.Equal(2, saved.Length);
        Assert.Contains(manual, saved);
        Assert.DoesNotContain(saved, root => root.Path == denied);
    }

    [Theory]
    [InlineData("music", true)]
    [InlineData("music,private", false)]
    [InlineData("private", true)]
    [InlineData("denied", false)]
    public async Task PromotionFolders_SetupUsesExactlyOneAccessibleRoot(string libraries, bool ready)
    {
        var first = Path.Combine(directory, "first-default");
        var second = Path.Combine(directory, "second-default");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var configured = FolderRoots([]);
        using var client = new HttpClient(new FolderResponseHandler(JsonSerializer.Serialize(new[]
        {
            new { ItemId = "music", Name = "Music", CollectionType = "music", Locations = new[] { first } },
            new { ItemId = "private", Name = "Private", CollectionType = "music", Locations = new[] { second } }
        })));
        var discovery = FolderDiscovery(configured, client);
        var access = new TestBackendLibraryAccess(factory, libraries.Split(','));
        var profiles = new PromotionProfiles(factory, configured, access);
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = FolderSession(false);
        var controller = new LibraryPromotionsController(service, configured, new(factory, new SystemPlatformClock()),
            libraryFolders: discovery, profiles: profiles)
        { ControllerContext = new() { HttpContext = http } };
        var setup = Assert.IsType<PromotionSetup>(Assert.IsType<OkObjectResult>(await controller.Setup(default)).Value);
        Assert.Equal(ready, setup.Ready);
        Assert.Equal(ready ? null : "library-folder-not-set", setup.Reason);
        Assert.Equal(setup.RootId, (await profiles.GetMineAsync(Context)).Values.LibraryRootId);
        var candidates = Assert.IsType<OkObjectResult>(await controller.Roots(default));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(candidates.Value));
        Assert.Equal(libraries == "denied" ? 0 : libraries.Split(',').Length, json.RootElement.GetArrayLength());
        Assert.DoesNotContain(directory, JsonSerializer.Serialize(candidates.Value));
    }

    [Fact]
    public async Task PromotionFolders_PersonalAndHouseholdSelectionsOverrideAutomaticDefaults()
    {
        await SetPromotionRoleAsync(true, false);
        var configured = FolderRoots([new(rootId, "Music", Path.Combine(directory, "library", "music"), BackendLibraryId: "music")]);
        var profiles = new PromotionProfiles(factory, configured, new TestBackendLibraryAccess(factory, "music"));
        await profiles.SaveHouseholdAsync(Context, new(new() { NamingConfirmed = true, LibraryRootId = rootId }));
        await profiles.SaveMineAsync(Context, new() { NamingConfirmed = true, NamingScript = "%title%.%_extension%" });
        Assert.Equal(rootId, (await profiles.GetMineAsync(Context)).Values.LibraryRootId);
        Assert.True((await profiles.SetupAsync(Context)).Ready);
    }

    private PromotionRoots FolderRoots(PromotionRoot[] approved, string? allowed = null, string? minimumFree = null)
    {
        var current = JsonSerializer.Serialize(approved);
        var durable = new Mock<IDurableRuntimeSettings>();
        durable.Setup(item => item.GetAsync(PromotionRoots.Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new EffectiveRuntimeSetting(PromotionRoots.Key, RuntimeSettingValueType.String,
                current, current, RuntimeSettingOrigin.Durable, 1, "test", null, null));
        durable.Setup(item => item.ApplyBatchAsync(It.IsAny<IReadOnlyList<RuntimeSettingWrite>>(), "promotion",
                It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<RuntimeSettingWrite>, string, Guid?, CancellationToken>((writes, _, _, _) => current = writes.Single().RawValue)
            .ReturnsAsync(new RuntimeSettingBatchResult([], 1));
        return new(durable.Object, factory, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Promotion:AllowedBase"] = allowed, ["Promotion:MinimumFreeBytes"] = minimumFree }).Build());
    }

    private static PromotionLibraryFolders FolderDiscovery(PromotionRoots configured, HttpClient client)
    {
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(item => item.CreateClient(JellyfinProxyService.HttpClientName)).Returns(client);
        var proxy = new JellyfinProxyService(clients.Object, Options.Create(new JellyfinSettings { Url = "http://jellyfin.test" }),
            new HttpContextAccessor(), NullLogger<JellyfinProxyService>.Instance, Mock.Of<IApplicationCache>(),
            Mock.Of<IMediaAssetResolver>(), new ConfigurationBuilder().Build());
        return new(proxy, configured);
    }

    private AdminAuthSession FolderSession(bool administrator) => new()
    {
        AllstarrUserId = owner,
        UserId = "principal",
        BackendType = "jellyfin",
        BackendInstanceId = "backend",
        SessionId = "folder-session",
        UserName = "Owner",
        IsAdministrator = administrator,
        JellyfinAccessToken = "fixture-folder-token",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };

    private LibraryPromotionsController FolderController(PromotionRoots configured, PromotionLibraryFolders discovery, bool administrator)
    {
        var http = new DefaultHttpContext();
        http.Items[AdminAuthSessionService.HttpContextSessionItemKey] = FolderSession(administrator);
        return new(service, configured, new(factory, new SystemPlatformClock()), libraryFolders: discovery)
        { ControllerContext = new() { HttpContext = http } };
    }

    private static string MusicFolderJson(string path) => JsonSerializer.Serialize(new[]
        { new { ItemId = "music", Name = "Music", CollectionType = "music", Locations = new[] { path } } });

    private sealed class FolderResponseHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Endpoint { get; private set; }
        public string Authorization { get; private set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Endpoint = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.GetValues("Authorization").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
