using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers.Ipinfo;
using IpLocation.Api.Tests.Common;
using MaxMind.Db;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>纯内存自造扁平 MMDB，验证 Lite 真实字段合同；不读取真实数据、凭据或原库。</summary>
public sealed class IpinfoProviderTests
{
    private const string Type = "ipinfo bundle_location_lite.mmdb";
    private const ulong Epoch = 1793606699;

    [Fact]
    public void DisabledAndUnconfiguredSourcesNeverOpenTheFile()
    {
        Reader Factory(string path) => throw new InvalidOperationException("must not open");
        using var disabled = Create(new IpinfoOptions(), Factory);
        using var missing = Create(new IpinfoOptions { Enabled = true, LicenseConfirmed = true }, Factory);
        Assert.Equal("disabled", disabled.Descriptor.Status);
        Assert.Equal("notReady", missing.Descriptor.Status);
    }

    [Fact]
    public async Task LegacyLicenseFlagDoesNotBlockValidDatabaseQueries()
    {
        using var provider = Create(new IpinfoOptions { Enabled = true, LicenseConfirmed = false, DatabasePath = "controlled.mmdb" }, _ => Reader(Record()));
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.LicenseConfirmed);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Theory]
    [InlineData("GeoLite2-City")]
    [InlineData("DBIP-City-Lite")]
    [InlineData("ipinfo location.mmdb")]
    public void OtherDatabaseTypesCannotMasqueradeAsLite(string type)
    {
        using var provider = Create(factory: _ => Reader(type: type));
        Assert.False(provider.Descriptor.Ready);
        Assert.Equal("unavailable", provider.Descriptor.Status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public async Task FlatSchemaReturnsAllSevenFieldsAndPreservesAsPrefixForBothFamilies(string address)
    {
        using var provider = Create(factory: _ => Reader(Record()));
        var result = await provider.QueryAsync(IPAddress.Parse(address), default);
        Assert.True(provider.Descriptor.Ready);
        Assert.True(provider.Descriptor.SupportsIpv4);
        Assert.True(provider.Descriptor.SupportsIpv6);
        Assert.Equal("2026-11-02T08:04:59Z", provider.Descriptor.Version);
        Assert.Equal("success", result.Status);
        Assert.Equal("CN", result.Fields.CountryCode);
        Assert.Equal("Controlled Country", result.Fields.Country);
        Assert.Equal("AS", result.Fields.ContinentCode);
        Assert.Equal("Asia", result.Fields.Continent);
        Assert.Equal("AS64512", result.Fields.Asn);
        Assert.Equal("Controlled Network", result.Fields.AsName);
        Assert.Equal("example.net", result.Fields.AsDomain);
        Assert.Null(result.Fields.Isp);
        Assert.Null(result.Fields.Region);
        Assert.Null(result.Fields.City);
        Assert.Null(result.Fields.Latitude);
        Assert.Null(result.Fields.Longitude);
        Assert.Null(result.Fields.PostalCode);
        Assert.Null(result.Fields.TimeZone);
        Assert.Equal("https://creativecommons.org/licenses/by-sa/4.0/", provider.Descriptor.LicenseUrl);
        Assert.Contains("IP address data is powered by IPinfo", provider.Descriptor.Attribution);
    }

    [Fact]
    public async Task AbsentAndEmptyFieldsRemainNullWithoutInventingLocationOrNetworkFields()
    {
        using var provider = Create(factory: _ => Reader(ControlledMmdb.Map(("country_code", "CN"), ("country", ""), ("asn", " "))));
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.Equal("success", result.Status);
        Assert.Equal("CN", result.Fields.CountryCode);
        Assert.Null(result.Fields.Country);
        Assert.Null(result.Fields.Asn);
        Assert.Null(result.Fields.AsName);
        Assert.Null(result.Fields.AsDomain);
        Assert.Null(result.Fields.ContinentCode);
        Assert.Null(result.Fields.Continent);
        Assert.Null(result.Fields.Isp);
    }

    [Fact]
    public async Task EmptyMatchedMapIsNotAFalseSuccessfulRecord()
    {
        using var provider = Create(factory: _ => Reader(ControlledMmdb.Map()));
        Assert.Equal("notFound", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public async Task NoMatchIsAnExplicitResultInsteadOfAnException(string address)
    {
        using var provider = Create();
        var result = await provider.QueryAsync(IPAddress.Parse(address), default);
        Assert.Equal("notFound", result.Status);
        Assert.Equal("2026-11-02T08:04:59Z", result.Version);
    }

    [Fact]
    public async Task Ipv4OnlyMetadataDoesNotClaimIpv6Support()
    {
        using var provider = Create(factory: _ => Reader(Record(), family: 4));
        Assert.False(provider.Descriptor.SupportsIpv6);
        var result = await provider.QueryAsync(IPAddress.Parse("2001:4860:4860::8888"), default);
        Assert.Equal("unsupported", result.Status);
        Assert.Null(result.Fields.Country);
    }

    [Theory]
    [InlineData("asn")]
    [InlineData("country")]
    [InlineData("continent_code")]
    public async Task WrongMmdbValueTypeIsSourceFailureNotAnEmptyOrCoercedField(string key)
    {
        var record = Record();
        record[key] = 123U;
        using var provider = Create(factory: _ => Reader(record));
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.Asn);
    }

    [Theory]
    [InlineData("country")]
    [InlineData("continent")]
    [InlineData("as_domain")]
    public async Task InvalidControlCharactersDoNotReachAnyPublicFormat(string key)
    {
        var record = Record();
        record[key] = "private\0unsafe-field";
        using var provider = Create(factory: _ => Reader(record));
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.DoesNotContain("private", result.Message);
        Assert.False(provider.Descriptor.Ready);
    }

    [Fact]
    public void MissingAndCorruptFileErrorsNeverExposeTheConfiguredPath()
    {
        using var missing = Create(factory: _ => throw new FileNotFoundException("/private/source/data.mmdb"));
        using var corrupt = Create(factory: _ => throw new InvalidDatabaseException("/private/source/data.mmdb"));
        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.Equal("unavailable", corrupt.Descriptor.Status);
        Assert.DoesNotContain("/private", missing.Descriptor.Message);
        Assert.DoesNotContain("/private", corrupt.Descriptor.Message);
    }

    [Fact]
    public async Task CancellationAndDisposalCannotReturnANormalRecord()
    {
        using var provider = Create(factory: _ => Reader(Record()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(IPAddress.Parse("8.8.8.8"), cancellation.Token));
        provider.Dispose();
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.False(provider.Descriptor.Ready);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("txt")]
    public async Task AllPublicFormatsIncludeNewFieldsAndAsDomainRemainsText(string format)
    {
        using var provider = Create(factory: _ => Reader(Record()));
        var query = new IpQueryService([provider], OptionsFactory.Create(new QueryOptions()));
        var response = await query.QueryAsync("8.8.8.8", "ipinfo", default);
        var content = QueryFormatter.Format(response, format).Content!;
        if (format == "json")
        {
            using var json = JsonDocument.Parse(content);
            var fields = json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("fields");
            Assert.Equal("Asia", fields.GetProperty("continent").GetString());
            Assert.Equal("AS", fields.GetProperty("continentCode").GetString());
            Assert.Equal("example.net", fields.GetProperty("asDomain").GetString());
        }
        else if (format == "xml")
        {
            var xml = XDocument.Parse(content);
            Assert.Equal("Asia", xml.Descendants("continent").Single().Value);
            Assert.Equal("example.net", xml.Descendants("asDomain").Single().Value);
        }
        else
        {
            Assert.Contains("sources.ipinfo.fields.continentCode=AS\n", content);
            Assert.Contains("sources.ipinfo.fields.asDomain=example.net\n", content);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("txt")]
    public async Task CorruptIpinfoRecordCannotBreakSuccessfulAggregateOrSerialization(string format)
    {
        var record = Record();
        record["continent"] = "unsafe\0field";
        using var broken = Create(factory: _ => Reader(record));
        var query = new IpQueryService([new HealthyProvider(), broken], OptionsFactory.Create(new QueryOptions()));
        var response = await query.QueryAsync("8.8.8.8", "all", default);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("partial", response.Result.Data!.Status);
        Assert.Equal("success", response.Result.Data.Sources.Single(source => source.Id == "dbip").Status);
        Assert.Equal("unavailable", response.Result.Data.Sources.Single(source => source.Id == "ipinfo").Status);
        var content = QueryFormatter.Format(response, format).Content!;
        Assert.DoesNotContain("unsafe", content);
        Assert.DoesNotContain('\0', content);
    }

    [Fact]
    public void ProviderRegistrationReplacesTheDisabledPlaceholderWithoutDuplicateSources()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddIpDataProviders();
        using var container = services.BuildServiceProvider();
        var providers = container.GetServices<IIpDataProvider>().ToArray();
        Assert.Equal(6, providers.Length);
        Assert.IsType<IpinfoLiteDataProvider>(Assert.Single(providers, provider => provider.Descriptor.Id == "ipinfo"));
    }

    private static IpinfoLiteDataProvider Create(IpinfoOptions? options = null, Func<string, Reader>? factory = null) =>
        new(OptionsFactory.Create(options ?? new IpinfoOptions { Enabled = true, LicenseConfirmed = true, DatabasePath = "controlled.mmdb" }), factory ?? (_ => Reader()));
    private static Reader Reader(Dictionary<string, object>? record = null, string type = Type, int family = 6) =>
        new(ControlledMmdb.Create(type, family, Epoch, record));
    private static Dictionary<string, object> Record() => ControlledMmdb.Map(("country_code", "CN"), ("country", "Controlled Country"),
        ("continent_code", "AS"), ("continent", "Asia"), ("asn", "AS64512"), ("as_name", "Controlled Network"), ("as_domain", "example.net"));

    private sealed class HealthyProvider : IIpDataProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("dbip", "Controlled source", "ready", true, true, true, true, true,
            "controlled-version", "Controlled", "https://example.net/license", "Controlled attribution");
        public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderQueryResult("success", new IpFields { CountryCode = "CN", Country = "受控国家" }, "Controlled"));
    }
}
