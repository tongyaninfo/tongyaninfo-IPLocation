using System.Net;
using IpLocation.Api.Tests.Common;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl.Providers.DbIp;
using MaxMind.GeoIP2;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>受控 MMDB 全部在内存生成，不把真实供应商记录或本机路径写入测试夹具。</summary>
public sealed class DbIpProviderTests
{
    [Fact]
    public void DisabledAndUnconfiguredSourcesNeverOpenAFile()
    {
        using var disabled = Create(new DbIpOptions(), _ => throw new InvalidOperationException("must not open"));
        using var missing = Create(new DbIpOptions { Enabled = true, LicenseConfirmed = true }, _ => throw new InvalidOperationException("must not open"));
        Assert.Equal("disabled", disabled.Descriptor.Status);
        Assert.Equal("notReady", missing.Descriptor.Status);
    }

    [Fact]
    public async Task LegacyLicenseFlagDoesNotBlockValidDatabaseQueries()
    {
        using var provider = Create(new DbIpOptions { Enabled = true, LicenseConfirmed = false, DatabasePath = "controlled.mmdb" },
            _ => Reader(record: Map(("country", Map(("iso_code", "CN"))))));
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.LicenseConfirmed);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Theory]
    [InlineData("GeoLite2-City")]
    [InlineData("DBIP-Country-Lite")]
    public void OtherMmdbTypesCannotBeActivatedAsDbIpCity(string type)
    {
        using var provider = Create(factory: _ => Reader(type));
        Assert.False(provider.Descriptor.Ready);
        Assert.Equal("unavailable", provider.Descriptor.Status);
    }

    [Fact]
    public async Task OfficialReaderAcceptsDbIpTypeAndPreservesChineseAndEnglishNames()
    {
        using var provider = Create(factory: _ => Reader(record: Map(
            ("country", Map(("iso_code", "CN"), ("names", Map(("zh-CN", "中国"), ("en", "China"))))),
            ("city", Map(("names", Map(("en", "Controlled City"))))),
            ("subdivisions", new object[] { Map(("names", Map(("en", "Outer Region")))), Map(("names", Map(("zh-CN", "受控省份")))) }),
            ("location", Map(("latitude", 0d), ("longitude", 0d))))));
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("2026-11-01T02:52:26Z", provider.Descriptor.Version);
        Assert.Equal("success", result.Status);
        Assert.Equal("中国", result.Fields.Country);
        Assert.Equal("CN", result.Fields.CountryCode);
        Assert.Equal("受控省份", result.Fields.Region);
        Assert.Equal("Controlled City", result.Fields.City);
        Assert.Equal(0d, result.Fields.Latitude);
        Assert.Equal(0d, result.Fields.Longitude);
        Assert.Null(result.Fields.Isp);
        Assert.Null(result.Fields.Asn);
        Assert.Null(result.Fields.PostalCode);
        Assert.Null(result.Fields.TimeZone);
    }

    [Fact]
    public async Task OriginalNameIsPreservedWhenNeitherChineseNorEnglishIsPresent()
    {
        using var provider = Create(factory: _ => Reader(record: Map(("country", Map(("names", Map(("fr", "Nom original"))))))));
        var result = await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default);
        Assert.Equal("Nom original", result.Fields.Country);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.City);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public async Task NoMatchIsAnExplicitResultForBothFamilies(string address)
    {
        using var provider = Create(factory: _ => Reader());
        var result = await provider.QueryAsync(IPAddress.Parse(address), default);
        Assert.Equal("notFound", result.Status);
    }

    [Fact]
    public async Task Ipv4OnlyDatabaseDoesNotClaimIpv6SupportAndCancellationIsHonored()
    {
        using var provider = Create(factory: _ => Reader(family: 4));
        Assert.False(provider.Descriptor.SupportsIpv6);
        Assert.Equal("unsupported", (await provider.QueryAsync(IPAddress.Parse("2001:4860:4860::8888"), default)).Status);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(IPAddress.Parse("8.8.8.8"), cancellation.Token));
    }

    [Fact]
    public void MissingOrInvalidFilesDoNotExposePrivatePaths()
    {
        using var missing = Create(factory: _ => throw new FileNotFoundException("private path /controlled-private/data.mmdb"));
        using var invalid = Create(factory: _ => throw new InvalidDataException("private path /controlled-private/data.mmdb"));
        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.Equal("unavailable", invalid.Descriptor.Status);
        Assert.DoesNotContain("controlled-private", missing.Descriptor.Message);
        Assert.DoesNotContain("controlled-private", invalid.Descriptor.Message);
    }

    private static DbIpCityDataProvider Create(DbIpOptions? options = null, Func<string, DatabaseReader>? factory = null) =>
        new(OptionsFactory.Create(options ?? new DbIpOptions { Enabled = true, LicenseConfirmed = true, DatabasePath = "controlled.mmdb" }), factory ?? (_ => Reader()));

    private static Dictionary<string, object> Map(params (string Key, object Value)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    private static DatabaseReader Reader(string type = "DBIP-City-Lite", int family = 6, Dictionary<string, object>? record = null) =>
        new(ControlledMmdb.Create(type, family, record: record), ["zh-CN", "en"]);
}
