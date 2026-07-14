using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace allstarr.Tests;

public class AdminNetworkBindingPolicyTests
{
    [Fact]
    public void ShouldBindAdminAnyIp_DefaultsToFalse_WhenUnset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var bindAnyIp = AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(configuration);

        Assert.False(bindAnyIp);
    }

    [Fact]
    public void ShouldBindAdminAnyIp_ReturnsTrue_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:BindAnyIp"] = "true"
            })
            .Build();

        var bindAnyIp = AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(configuration);

        Assert.True(bindAnyIp);
    }

    [Fact]
    public void ShouldListenAdminAnyIp_ReturnsTrue_ForContainerizedHostMapping()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:BindAnyIp"] = "false",
                ["Admin:Containerized"] = "true"
            })
            .Build();

        Assert.True(AdminNetworkBindingPolicy.ShouldListenAdminAnyIp(configuration));
        Assert.False(AdminNetworkBindingPolicy.ShouldBindAdminAnyIp(configuration));
    }

    [Fact]
    public void ParseTrustedSubnets_ReturnsOnlyValidNetworks()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:TrustedSubnets"] = "192.168.1.0/24, invalid,10.0.0.0/8"
            })
            .Build();

        var subnets = AdminNetworkBindingPolicy.ParseTrustedSubnets(configuration);

        Assert.Equal(2, subnets.Count);
        Assert.Contains(subnets, n => n.ToString() == "192.168.1.0/24");
        Assert.Contains(subnets, n => n.ToString() == "10.0.0.0/8");
    }

    [Fact]
    public void ParseLinuxDefaultGateways_DecodesProcRouteLittleEndianAddress()
    {
        var routes = new[]
        {
            "Iface Destination Gateway Flags RefCnt Use Metric Mask MTU Window IRTT",
            "eth0 00000000 01D7A8C0 0003 0 0 0 00000000 0 0 0"
        };

        var gateways = AdminNetworkBindingPolicy.ParseLinuxDefaultGateways(routes);

        Assert.Contains(IPAddress.Parse("192.168.215.1"), gateways);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.55", true)]
    [InlineData("10.25.1.3", false)]
    public void IsRemoteIpAllowed_HonorsLoopbackAndTrustedSubnets(string ip, bool expected)
    {
        var trusted = new List<IPNetwork>();
        Assert.True(IPNetwork.TryParse("192.168.1.0/24", out var subnet));
        trusted.Add(subnet);

        var allowed = AdminNetworkBindingPolicy.IsRemoteIpAllowed(IPAddress.Parse(ip), trusted);

        Assert.Equal(expected, allowed);
    }
}
