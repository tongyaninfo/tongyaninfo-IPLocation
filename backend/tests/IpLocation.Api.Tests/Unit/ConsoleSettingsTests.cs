using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Unit;

public sealed class ConsoleSettingsTests
{
    [Fact]
    public void NetworksNormalizeMappedAndExactAddressesAndDeduplicate()
    {
        Assert.True(ConsoleSettingsService.TryNormalizeNetworks(
            ["127.0.0.1", "::ffff:127.0.0.1", "192.0.2.0/24", "2001:db8::/48"], out var networks));
        Assert.Equal(3, networks.Length);
        Assert.Contains(networks, network => network.ToString() == "127.0.0.1/32");
        Assert.Contains(networks, network => network.Contains(IPAddress.Parse("192.0.2.8")));
        Assert.DoesNotContain(networks, network => network.Contains(IPAddress.Parse("203.0.113.188")));
    }

    [Theory]
    [InlineData("https://127.0.0.1")]
    [InlineData("127.0.0.1:5080")]
    [InlineData("192.0.2.1/24")]
    [InlineData("bad-ip")]
    [InlineData("")]
    public void InvalidNetworksDoNotExpandAccess(string input) =>
        Assert.False(ConsoleSettingsService.TryNormalizeNetworks([input], out _));

    [Fact]
    public void QueryLimitsRequireConsistentDeadlines()
    {
        var query = new QueryOptions();
        Assert.True(ConsoleRuntimeSettings.IsValidQuery(query));
        query.TotalTimeoutMs = 1;
        Assert.False(ConsoleRuntimeSettings.IsValidQuery(query));
        query.TotalTimeoutMs = 5_000;
        query.MaxConcurrentSources = 7;
        Assert.False(ConsoleRuntimeSettings.IsValidQuery(query));
    }

    [Fact]
    public void RuntimePublicationImmediatelyRevokesAndEmptyPersistedListDeniesAll()
    {
        using var services = Services(false);
        var runtime = services.GetRequiredService<IConsoleRuntimeSettings>();
        var access = services.GetRequiredService<IConsoleAccessService>();
        Assert.True(access.IsAllowed(IPAddress.Loopback));
        runtime.Publish(new(2, new QueryOptions(), ["192.0.2.0/24"], true));
        Assert.False(access.IsAllowed(IPAddress.Loopback));
        Assert.True(access.IsAllowed(IPAddress.Parse("192.0.2.4")));
        runtime.Publish(new(3, new QueryOptions(), [], true));
        Assert.False(access.IsAllowed(IPAddress.Parse("192.0.2.4")));
        // 已完成的慢读取不能覆盖之后已提交的安全收紧。
        runtime.Publish(new(2, new QueryOptions(), ["127.0.0.1"], true));
        Assert.False(access.IsAllowed(IPAddress.Loopback));
        Assert.Equal(3, runtime.Current.Revision);
    }

    [Fact]
    public void DatabaseEnabledButNotLoadedKeepsConsoleClosed()
    {
        using var services = Services(true);
        var runtime = services.GetRequiredService<IConsoleRuntimeSettings>();
        var access = services.GetRequiredService<IConsoleAccessService>();
        Assert.False(runtime.Current.IsReady);
        Assert.False(access.IsAllowed(IPAddress.Loopback));
        runtime.Publish(new(1, new QueryOptions(), ["127.0.0.1"], true));
        Assert.True(access.IsAllowed(IPAddress.Loopback));
    }

    [Fact]
    public void LegacyDefaultCodeIsPublishedAsCanonicalWithoutStorageMigration()
    {
        using var services = Services(false);
        var runtime = services.GetRequiredService<IConsoleRuntimeSettings>();
        runtime.Publish(new(2, new QueryOptions { DefaultDatabase = "chunzhen" }, ["127.0.0.1"], true));
        Assert.Equal("CZ88", runtime.Current.Query.DefaultDatabase);
        Assert.Equal("provider.chunzhen.enabled", ProviderCodes.EnabledSettingKey(runtime.Current.Query.DefaultDatabase));
    }

    private static ServiceProvider Services(bool databaseEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Console:AllowedNetworks:0"] = "127.0.0.1"
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<QueryOptions>();
        services.Configure<DatabaseOptions>(value => value.Enabled = databaseEnabled);
        services.AddConsoleAccess(configuration);
        services.AddSingleton<IConsoleRuntimeSettings, ConsoleRuntimeSettings>();
        return services.BuildServiceProvider();
    }
}
