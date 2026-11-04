using IpLocation.Api.Common;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class ProviderCodesTests
{
    [Theory]
    [InlineData("CZ88")]
    [InlineData("cz88")]
    [InlineData("chunzhen")]
    [InlineData("CHUNZHEN")]
    public void PublicRenameKeepsExistingVersionActivationAndEnabledStorageIdentity(string alias)
    {
        Assert.Equal("CZ88", ProviderCodes.Public(alias));
        Assert.Equal("chunzhen", ProviderCodes.Storage(alias));
        Assert.Equal("provider.chunzhen.enabled", ProviderCodes.EnabledSettingKey(alias));
        Assert.Equal(new[] { "CZ88", "chunzhen", "cz88" }, ProviderCodes.LogAliases(alias));
    }

    [Fact]
    public void LegacyAndCanonicalStateNamesShareOneRuntimeRevision()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var files = new ProviderFileFactory(OptionsFactory.Create(new GeoLite2Options()), OptionsFactory.Create(new Ip2LocationOptions()),
            OptionsFactory.Create(new DbIpOptions()), OptionsFactory.Create(new Ip2RegionOptions()), OptionsFactory.Create(new IpinfoOptions()),
            TimeProvider.System, OptionsFactory.Create(new ChunzhenOptions()));
        var oldDescriptor = new ProviderDescriptor("chunzhen", "纯真", "notReady", true, false, false, false, false, null, "notReady", "", "");
        using var registry = new RuntimeProviderRegistry([new UnconfiguredIpDataProvider(oldDescriptor)], services, files);
        registry.PublishState("CZ88", true, 11);
        Assert.Equal(11, registry.Revision("chunzhen"));
        Assert.Equal("CZ88", registry.Providers.Single().Descriptor.Id);
        Assert.True(registry.Providers.Single().Descriptor.Enabled);
        registry.PublishState("chunzhen", false, 12);
        Assert.Equal(12, registry.Revision("CZ88"));
        Assert.False(registry.Providers.Single().Descriptor.Enabled);
        Assert.True(files.Supports("CZ88")); Assert.True(files.Supports("chunzhen"));
    }
}
