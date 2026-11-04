using System.Net;
using System.Text;
using IpLocation.Api.Common;
using IpLocation.Api.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Unit;

/// <summary>使用真实默认配置和各类覆盖来源，验证名单收紧不会保留旧网段。</summary>
public sealed class ConsoleAccessTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.0.2.0", true)]
    [InlineData("192.0.2.1", true)]
    [InlineData("192.0.2.254", true)]
    [InlineData("::ffff:198.51.100.25", true)]
    [InlineData("198.51.100.0", true)]
    [InlineData("198.51.100.254", true)]
    [InlineData("127.0.0.2", false)]
    [InlineData("203.0.113.187", false)]
    [InlineData("203.0.113.189", false)]
    [InlineData("203.0.113.199", false)]
    [InlineData("203.0.113.201", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("203.0.113.27", false)]
    [InlineData("2001:db8::1", false)]
    public void DefaultsAllowOnlyRequestedLocalAddresses(string address, bool expected)
    {
        using var configuration = (ConfigurationRoot)DefaultConfiguration().Build();
        using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
        Assert.Equal(expected, services.GetRequiredService<IConsoleAccessService>().IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    public void ExplicitEmptyOrNullJsonListDeniesEvenDefaultAddresses(string list)
    {
        using var configuration = (ConfigurationRoot)DefaultConfiguration().AddJsonStream(JsonList(list)).Build();
        using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
        var access = services.GetRequiredService<IConsoleAccessService>();
        Assert.False(access.IsAllowed(IPAddress.Loopback));
        Assert.False(access.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(access.IsAllowed(IPAddress.Parse("192.0.2.1")));
        Assert.False(access.IsAllowed(IPAddress.Parse("198.51.100.1")));
        Assert.False(access.IsAllowed(null));
    }

    [Fact]
    public void HigherPriorityJsonListReplacesRatherThanMergesDefaults()
    {
        using var configuration = (ConfigurationRoot)DefaultConfiguration().AddJsonStream(JsonList("[\"10.20.30.40\"]")).Build();
        using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
        AssertOnlyCustomAddress(services.GetRequiredService<IConsoleAccessService>(), "10.20.30.40");
    }

    [Fact]
    public void CommandLineListReplacesRatherThanMergesDefaults()
    {
        using var configuration = (ConfigurationRoot)DefaultConfiguration()
            .AddCommandLine(["--Console:AllowedNetworks:0=10.20.30.40"]).Build();
        using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
        AssertOnlyCustomAddress(services.GetRequiredService<IConsoleAccessService>(), "10.20.30.40");
    }

    [Fact]
    public void EnvironmentListReplacesRatherThanMergesDefaults()
    {
        var prefix = $"IPLOCATION_CONSOLE_TEST_{Guid.NewGuid():N}_";
        var key = prefix + "Console__AllowedNetworks__0";
        Environment.SetEnvironmentVariable(key, "10.20.30.40");
        try
        {
            using var configuration = (ConfigurationRoot)DefaultConfiguration().AddEnvironmentVariables(prefix).Build();
            using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
            AssertOnlyCustomAddress(services.GetRequiredService<IConsoleAccessService>(), "10.20.30.40");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void ReloadImmediatelyRevokesPreviouslyAllowedAddress()
    {
        using var configuration = (ConfigurationRoot)DefaultConfiguration().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Console:AllowedNetworks:0"] = "10.20.30.40"
        }).Build();
        using var services = new ServiceCollection().AddConsoleAccess(configuration).BuildServiceProvider();
        var access = services.GetRequiredService<IConsoleAccessService>();
        Assert.True(access.IsAllowed(IPAddress.Parse("10.20.30.40")));

        configuration.Providers.Last().Set("Console:AllowedNetworks:0", "10.20.30.41");
        configuration.Reload();

        Assert.False(access.IsAllowed(IPAddress.Parse("10.20.30.40")));
        Assert.True(access.IsAllowed(IPAddress.Parse("10.20.30.41")));
        Assert.False(access.IsAllowed(IPAddress.Loopback));
    }

    private static IConfigurationBuilder DefaultConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json");

    private static MemoryStream JsonList(string list) =>
        new(Encoding.UTF8.GetBytes($"{{\"Console\":{{\"AllowedNetworks\":{list}}}}}"));

    private static void AssertOnlyCustomAddress(IConsoleAccessService access, string address)
    {
        Assert.True(access.IsAllowed(IPAddress.Parse(address)));
        Assert.False(access.IsAllowed(IPAddress.Loopback));
        Assert.False(access.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(access.IsAllowed(IPAddress.Parse("192.0.2.1")));
        Assert.False(access.IsAllowed(IPAddress.Parse("198.51.100.1")));
    }
}
