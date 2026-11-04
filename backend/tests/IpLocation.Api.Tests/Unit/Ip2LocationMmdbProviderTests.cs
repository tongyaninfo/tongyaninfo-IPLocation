using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers.Ip2Location;
using IpLocation.Api.Tests.Common;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>仅使用内存自造的标准 City MMDB；不读取、落盘或清理真实供应商数据库。</summary>
public sealed class Ip2LocationMmdbProviderTests
{
    private const ulong Epoch = 1793470901;

    [Fact]
    public void DisabledAndUnsetPathsNeverOpenTheDatabase()
    {
        DatabaseReader Factory(string path) => throw new InvalidOperationException("must not open");
        using var disabled = Create(new Ip2LocationOptions { Enabled = false, DatabasePath = "controlled.mmdb" }, Factory);
        using var unset = Create(new Ip2LocationOptions(), Factory);

        Assert.Equal("disabled", disabled.Descriptor.Status);
        Assert.Equal("notReady", unset.Descriptor.Status);
        Assert.False(disabled.Descriptor.Ready);
        Assert.False(unset.Descriptor.Ready);
    }

    [Fact]
    public async Task LegacyLicenseFlagDoesNotBlockValidDatabaseQueries()
    {
        using var provider = Create(new Ip2LocationOptions { Enabled = true, LicenseConfirmed = false, DatabasePath = "controlled.mmdb" }, _ => Reader(Record()));
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.LicenseConfirmed);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Theory]
    [InlineData("GeoLite2-Country")]
    [InlineData("DBIP-City-Lite")]
    [InlineData("ipinfo bundle_location_lite.mmdb")]
    public void IncompatibleDatabaseSchemasCannotBecomeReady(string type)
    {
        using var provider = Create(factory: _ => Reader(type: type));

        Assert.Equal("unavailable", provider.Descriptor.Status);
        Assert.False(provider.Descriptor.Ready);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public async Task CityTreeMetadataDoesNotTurnTheIpv4DownloadIntoIpv6Coverage(int family)
    {
        using var provider = Create(factory: _ => Reader(Record(), family: family));

        Assert.True(provider.Descriptor.Ready);
        Assert.True(provider.Descriptor.SupportsIpv4);
        Assert.False(provider.Descriptor.SupportsIpv6);
        Assert.Equal("unsupported", (await provider.QueryAsync(IPAddress.Parse("2001:4860:4860::8888"), default)).Status);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("::ffff:8.8.8.8")]
    public async Task StandardCityFieldsUseChineseThenEnglishAndPreserveSourceValues(string address)
    {
        using var provider = Create(factory: _ => Reader(Record()));

        var result = await provider.QueryAsync(IPAddress.Parse(address), default);

        Assert.Equal("success", result.Status);
        Assert.Equal("CN", result.Fields.CountryCode);
        Assert.Equal("受控国家", result.Fields.Country);
        Assert.Equal("AS", result.Fields.ContinentCode);
        Assert.Equal("受控大洲", result.Fields.Continent);
        Assert.Equal("受控省份", result.Fields.Region);
        Assert.Equal("Controlled City", result.Fields.City);
        Assert.Equal("00001", result.Fields.PostalCode);
        Assert.Equal("Asia/Shanghai", result.Fields.TimeZone);
        Assert.Equal(0d, result.Fields.Latitude);
        Assert.Equal(0d, result.Fields.Longitude);
        Assert.Null(result.Fields.Isp);
        Assert.Null(result.Fields.Asn);
        Assert.Null(result.Fields.AsName);
        Assert.Equal("ip2location", provider.Descriptor.Id);
        Assert.Contains("IP2Location LITE database", provider.Descriptor.Attribution);
        Assert.DoesNotContain("MaxMind", provider.Descriptor.Attribution);
    }

    [Fact]
    public async Task OriginalLanguageRemainsAvailableWhenChineseAndEnglishAreMissing()
    {
        var record = Map(("country", Map(("iso_code", "FR"), ("names", Map(("fr", "Pays original"))))),
            ("city", Map(("names", Map(("fr", "Ville originale"))))));
        using var provider = Create(factory: _ => Reader(record));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("success", result.Status);
        Assert.Equal("Pays original", result.Fields.Country);
        Assert.Equal("Ville originale", result.Fields.City);
    }

    [Fact]
    public async Task MissingAndPlaceholderFieldsStayNullWithoutOtherSourceFallback()
    {
        var record = Map(("country", Map(("iso_code", "CN"), ("names", Map(("en", "-"))))),
            ("city", Map(("names", Map(("en", " "))))),
            ("subdivisions", new object[] { Map(("names", Map(("en", "-")))) }),
            ("postal", Map(("code", "-"))), ("location", Map(("time_zone", "-"))));
        using var provider = Create(factory: _ => Reader(record));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("success", result.Status);
        Assert.Equal("CN", result.Fields.CountryCode);
        Assert.Null(result.Fields.Country);
        Assert.Null(result.Fields.Region);
        Assert.Null(result.Fields.City);
        Assert.Null(result.Fields.Continent);
        Assert.Null(result.Fields.Latitude);
        Assert.Null(result.Fields.Longitude);
        Assert.Null(result.Fields.PostalCode);
        Assert.Null(result.Fields.TimeZone);
        Assert.Null(result.Fields.Isp);
        Assert.Null(result.Fields.Asn);
    }

    [Fact]
    public async Task RegisteredCountryAndEmptyRecordsCannotInventALocation()
    {
        using var empty = Create(factory: _ => Reader(Map()));
        using var registeredOnly = Create(factory: _ => Reader(Map(
            ("registered_country", Map(("iso_code", "CN"), ("names", Map(("en", "Registered Country"))))),
            ("location", Map(("latitude", 0d), ("longitude", 0d))))));

        var result = await registeredOnly.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("notFound", result.Status);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.Country);
        Assert.Equal("notFound", (await empty.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Fact]
    public async Task PartialCityRecordRemainsSuccessfulWithoutInventingACountry()
    {
        using var provider = Create(factory: _ => Reader(Map(
            ("city", Map(("names", Map(("en", "Controlled City"))))),
            ("location", Map(("latitude", 0d), ("longitude", 0d))))));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("success", result.Status);
        Assert.Equal("Controlled City", result.Fields.City);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.Country);
        Assert.Equal(0d, result.Fields.Latitude);
        Assert.Equal(0d, result.Fields.Longitude);
    }

    [Fact]
    public async Task ContinentOnlyRecordIsUsefulAndDoesNotRequireCountryOrCity()
    {
        using var provider = Create(factory: _ => Reader(Map(
            ("continent", Map(("code", "AS"), ("names", Map(("en", "Controlled Continent"))))))));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("success", result.Status);
        Assert.Equal("AS", result.Fields.ContinentCode);
        Assert.Equal("Controlled Continent", result.Fields.Continent);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.Country);
        Assert.Null(result.Fields.City);
    }

    [Fact]
    public async Task NoMatchIsExplicitAndKeepsTheMmdbBuildVersion()
    {
        using var provider = Create();

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("notFound", result.Status);
        Assert.Equal("2026-10-31T18:21:41Z", result.Version);
    }

    [Theory]
    [InlineData("latitude")]
    [InlineData("longitude")]
    public async Task ASingleZeroCoordinateIsPreservedWithoutInventingTheMissingCoordinate(string coordinate)
    {
        using var provider = Create(factory: _ => Reader(Map(("location", Map((coordinate, 0d))))));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("success", result.Status);
        Assert.Equal(coordinate == "latitude" ? 0d : (double?)null, result.Fields.Latitude);
        Assert.Equal(coordinate == "longitude" ? 0d : (double?)null, result.Fields.Longitude);
        Assert.Null(result.Fields.Country);
    }

    [Fact]
    public void MetadataVersionDoesNotReuseCsvVersionOrContentHashSettings()
    {
        using var provider = Create(new Ip2LocationOptions
        {
            DatabasePath = "controlled.mmdb", DataVersion = "old-csv-version", UseContentHashAsVersion = true,
            MaxRecordBytes = 0, MaxIndexEntries = 0
        }, _ => Reader());

        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("2026-10-31T18:21:41Z", provider.Descriptor.Version);
    }

    [Theory]
    [InlineData(91d, 0d)]
    [InlineData(0d, 181d)]
    [InlineData(double.NaN, 0d)]
    [InlineData(0d, double.PositiveInfinity)]
    public async Task InvalidCoordinatesFailTheSourceWithoutPublishingUnsafeNumbers(double latitude, double longitude)
    {
        var record = Record();
        record["location"] = Map(("latitude", latitude), ("longitude", longitude));
        using var provider = Create(factory: _ => Reader(record));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("unavailable", result.Status);
        Assert.Null(result.Fields.Latitude);
        Assert.False(provider.Descriptor.Ready);
    }

    [Theory]
    [InlineData("unsafe\0city")]
    [InlineData("unsafe\u001fcity")]
    [InlineData("unsafe\ufffecity")]
    public async Task InvalidTextCannotReachPublicFormatsOrExposeRawData(string value)
    {
        var record = Record();
        record["city"] = Map(("names", Map(("en", value))));
        using var provider = Create(factory: _ => Reader(record));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("unavailable", result.Status);
        Assert.DoesNotContain("unsafe", result.Message);
        Assert.Null(result.Fields.City);
        Assert.False(provider.Descriptor.Ready);
    }

    [Fact]
    public async Task InvalidMmdbFieldTypesAreFailuresRatherThanEmptyOrCoercedValues()
    {
        var record = Record();
        record["country"] = 123U;
        using var provider = Create(factory: _ => Reader(record));

        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);

        Assert.Equal("unavailable", result.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.Null(result.Fields.CountryCode);
    }

    [Fact]
    public void FileErrorsAreDistinguishedAndNeverRevealPrivatePaths()
    {
        using var missing = Create(factory: _ => throw new FileNotFoundException("private /controlled-private/data.mmdb"));
        using var invalid = Create(factory: _ => throw new InvalidDatabaseException("private /controlled-private/data.mmdb"));

        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.Equal("unavailable", invalid.Descriptor.Status);
        Assert.DoesNotContain("controlled-private", missing.Descriptor.Message);
        Assert.DoesNotContain("controlled-private", invalid.Descriptor.Message);
    }

    [Fact]
    public async Task CancellationAndRepeatedDisposalCannotReturnANormalRecord()
    {
        using var provider = Create(factory: _ => Reader(Record()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(IPAddress.Parse("8.8.8.8"), cancellation.Token));
        provider.Dispose();
        provider.Dispose();

        Assert.False(provider.Descriptor.Ready);
        Assert.Equal("unavailable", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Fact]
    public async Task ConcurrentLookupsUseTheSameReadOnlyReaderSafely()
    {
        using var provider = Create(factory: _ => Reader(Record()));

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
            provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default))));

        Assert.All(results, result =>
        {
            Assert.Equal("success", result.Status);
            Assert.Equal("Controlled City", result.Fields.City);
            Assert.Equal("受控国家", result.Fields.Country);
        });
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("txt")]
    public async Task TheExistingThreePublicFormatsPreserveCityFields(string format)
    {
        using var provider = Create(factory: _ => Reader(Record()));
        var query = new IpQueryService([provider], OptionsFactory.Create(new QueryOptions()));

        var response = await query.QueryAsync("8.8.8.8", "ip2location", default);
        var content = QueryFormatter.Format(response, format).Content!;

        Assert.Equal(200, response.StatusCode);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(content);
            var fields = json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("fields");
            Assert.Equal("受控国家", fields.GetProperty("country").GetString());
            Assert.Equal("00001", fields.GetProperty("postalCode").GetString());
        }
        else if (format == "xml")
        {
            var xml = XDocument.Parse(content);
            Assert.Equal("受控国家", xml.Descendants("country").Single().Value);
            Assert.Equal("Asia/Shanghai", xml.Descendants("timeZone").Single().Value);
        }
        else
        {
            Assert.Contains("sources.ip2location.fields.city=Controlled City\n", content);
            Assert.Contains("sources.ip2location.fields.postalCode=00001\n", content);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("txt")]
    public async Task ACorruptRecordDoesNotBreakTheSuccessfulSourcesOrSerialization(string format)
    {
        var record = Record();
        record["city"] = Map(("names", Map(("en", "unsafe\0record"))));
        using var broken = Create(factory: _ => Reader(record));
        var query = new IpQueryService([new HealthyProvider(), broken], OptionsFactory.Create(new QueryOptions()));

        var response = await query.QueryAsync("8.8.8.8", "all", default);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("partial", response.Result.Data!.Status);
        Assert.Equal("success", response.Result.Data.Sources.Single(source => source.Id == "dbip").Status);
        Assert.Equal("unavailable", response.Result.Data.Sources.Single(source => source.Id == "ip2location").Status);
        var content = QueryFormatter.Format(response, format).Content!;
        Assert.DoesNotContain("unsafe", content);
        Assert.DoesNotContain('\0', content);
    }

    [Fact]
    public void RuntimeRegistrationUsesOnlyOneIp2LocationMmdbProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddIpDataProviders();
        using var container = services.BuildServiceProvider();

        var providers = container.GetServices<IIpDataProvider>().ToArray();

        Assert.Equal(6, providers.Length);
        Assert.IsType<Ip2LocationMmdbDataProvider>(Assert.Single(providers, provider => provider.Descriptor.Id == "ip2location"));
        Assert.DoesNotContain(providers, provider => provider is Ip2LocationCsvDataProvider);
    }

    private static Ip2LocationMmdbDataProvider Create(Ip2LocationOptions? options = null, Func<string, DatabaseReader>? factory = null) =>
        new(OptionsFactory.Create(options ?? new Ip2LocationOptions { DatabasePath = "controlled.mmdb" }), factory ?? (_ => Reader()));

    private static DatabaseReader Reader(Dictionary<string, object>? record = null, string type = "GeoLite2-City", int family = 6) =>
        new(ControlledMmdb.Create(type, family, Epoch, record), ["zh-CN", "en"]);

    private static Dictionary<string, object> Map(params (string Key, object Value)[] entries) => ControlledMmdb.Map(entries);

    private static Dictionary<string, object> Record() => Map(
        ("country", Map(("iso_code", "CN"), ("names", Map(("zh-CN", "受控国家"), ("en", "Controlled Country"))))),
        ("continent", Map(("code", "AS"), ("names", Map(("zh-CN", "受控大洲"), ("en", "Controlled Continent"))))),
        ("subdivisions", new object[] { Map(("names", Map(("en", "Outer Region")))), Map(("names", Map(("zh-CN", "受控省份")))) }),
        ("city", Map(("names", Map(("en", "Controlled City"))))),
        ("postal", Map(("code", "00001"))),
        ("location", Map(("latitude", 0d), ("longitude", 0d), ("time_zone", "Asia/Shanghai"))));

    private sealed class HealthyProvider : IIpDataProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("dbip", "Controlled source", "ready", true, true, true, true, true,
            "controlled-version", "Controlled", "https://example.net/license", "Controlled attribution");

        public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderQueryResult("success", new IpFields { CountryCode = "CN", Country = "受控国家" }, "Controlled"));
    }
}
