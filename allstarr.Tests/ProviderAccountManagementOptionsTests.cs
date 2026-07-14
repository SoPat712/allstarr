using allstarr.Core.Identity;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class ProviderAccountManagementOptionsTests
{
    [Fact]
    public void Default_IsHybridAndSeparateFromIdentityMode()
    {
        var options = new ProviderAccountManagementOptions();

        Assert.Equal(ProviderAccountManagementMode.Hybrid, options.ParseManagementMode());
        Assert.NotEqual(IdentityOptions.SectionName, ProviderAccountManagementOptions.SectionName);
    }

    [Theory]
    [InlineData("AdminManaged", ProviderAccountManagementMode.AdminManaged)]
    [InlineData("usermanaged", ProviderAccountManagementMode.UserManaged)]
    [InlineData("HYBRID", ProviderAccountManagementMode.Hybrid)]
    [InlineData("  Hybrid  ", ProviderAccountManagementMode.Hybrid)]
    public void ParseManagementMode_AcceptsEverySupportedMode(
        string configured,
        ProviderAccountManagementMode expected)
    {
        var options = new ProviderAccountManagementOptions { ManagementMode = configured };

        Assert.Equal(expected, options.ParseManagementMode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("EveryoneManaged")]
    public void ParseManagementMode_RejectsAnythingExceptNamedSupportedModes(string configured)
    {
        var options = new ProviderAccountManagementOptions { ManagementMode = configured };

        var error = Assert.Throws<InvalidOperationException>(() => options.ParseManagementMode());

        Assert.Contains("ProviderAccounts:ManagementMode", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPlatformIdentity_RejectsInvalidManagementModeAtRegistration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProviderAccounts:ManagementMode"] = "EveryoneManaged"
            })
            .Build();
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddPlatformIdentity(configuration));

        Assert.Contains("ProviderAccounts:ManagementMode", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SingleUser", "AdminManaged")]
    [InlineData("Hybrid", "UserManaged")]
    [InlineData("Strict", "Hybrid")]
    public void AddPlatformIdentity_BindsIdentityAndAccountManagementModesIndependently(
        string identityMode,
        string accountManagementMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Mode"] = identityMode,
                ["ProviderAccounts:ManagementMode"] = accountManagementMode
            })
            .Build();
        var services = new ServiceCollection();

        services.AddPlatformIdentity(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            Enum.Parse<MultiUserMode>(identityMode),
            provider.GetRequiredService<IdentityOptions>().ParseMode());
        Assert.Equal(
            Enum.Parse<ProviderAccountManagementMode>(accountManagementMode),
            provider.GetRequiredService<ProviderAccountManagementOptions>().ParseManagementMode());
    }

    [Fact]
    public void RuntimeEnvMapping_UsesItsOwnProviderAccountConfigurationKey()
    {
        var providerAccountMapping = Assert.Single(RuntimeEnvConfiguration.MapEnvVarToConfiguration(
            "ALLSTARR_PROVIDER_ACCOUNT_MANAGEMENT_MODE",
            "AdminManaged"));
        var identityMapping = Assert.Single(RuntimeEnvConfiguration.MapEnvVarToConfiguration(
            "ALLSTARR_MULTI_USER_MODE",
            "Strict"));

        Assert.Equal("ProviderAccounts:ManagementMode", providerAccountMapping.Key);
        Assert.Equal("AdminManaged", providerAccountMapping.Value);
        Assert.Equal("Identity:Mode", identityMapping.Key);
        Assert.Equal("Strict", identityMapping.Value);
    }
}
