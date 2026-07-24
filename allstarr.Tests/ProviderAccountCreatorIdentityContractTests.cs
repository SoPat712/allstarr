namespace allstarr.Tests;

public sealed class ProviderAccountCreatorIdentityContractTests
{
    private readonly string entities = File.ReadAllText(
        FindRepositoryFile("allstarr", "Core", "Storage", "DurableEntities.cs"));
    private readonly string context = File.ReadAllText(
        FindRepositoryFile("allstarr", "Core", "Storage", "AllstarrDbContext.cs"));
    private readonly string controller = File.ReadAllText(
        FindRepositoryFile("allstarr", "Controllers", "ProviderAccountsController.cs"));

    [Fact]
    public void ProviderAccounts_PersistCreatorSeparatelyFromAudienceOwner()
    {
        Assert.Contains("public Guid? CreatedByUserId { get; set; }", entities, StringComparison.Ordinal);
        Assert.Contains("HasForeignKey(item => item.CreatedByUserId)", context, StringComparison.Ordinal);
        Assert.Contains("CreatedByUserId = session.AllstarrUserId", controller, StringComparison.Ordinal);

        var audienceStart = controller.IndexOf("public async Task<IActionResult> UpdateAudience(", StringComparison.Ordinal);
        var audienceEnd = controller.IndexOf("private bool TryGetSession(", audienceStart, StringComparison.Ordinal);
        var audience = controller[audienceStart..audienceEnd];
        Assert.DoesNotContain("account.CreatedByUserId =", audience, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountProjection_ExposesCreatorAndSourceDisplayNames()
    {
        Assert.Contains("account.CreatedByUserId", controller, StringComparison.Ordinal);
        Assert.Contains("creatorDisplayName", controller, StringComparison.Ordinal);
        Assert.Contains("sourceDisplayName = SourceDisplayName(account, creatorDisplayName)", controller, StringComparison.Ordinal);
        Assert.Contains("$\"{name} · {creatorDisplayName}\"", controller, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
