using allstarr.Core.Identity;
using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class ProviderAccountOptionsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Registration_DefaultsListenerConnectionsOnAndHonorsConfiguredToggle(string? configured, bool expected)
    {
        var values = new Dictionary<string, string?>();
        if (configured != null) values[ProviderAccountOptions.ListenerConnectionsKey] = configured;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddPlatformIdentity(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<ProviderAccountOptions>().ListenersCanConnectOwnAccounts);
    }

    [Fact]
    public void EnvironmentToggle_MapsToTheHouseholdAccountSetting()
    {
        var mapping = Assert.Single(RuntimeEnvConfiguration.MapEnvVarToConfiguration(
            "LISTENERS_CAN_CONNECT_OWN_ACCOUNTS", "false"));
        Assert.Equal(ProviderAccountOptions.ListenerConnectionsKey, mapping.Key);
        Assert.Equal("false", mapping.Value);
    }
}
