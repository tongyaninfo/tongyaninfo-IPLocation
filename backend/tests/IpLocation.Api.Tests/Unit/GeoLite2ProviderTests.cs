using System.Net;
using System.Text;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl.Providers.GeoLite2;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class GeoLite2ProviderTests
{
    private static readonly DateTimeOffset BuildTime = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledSourceNeverOpensTheFile()
    {
        using var provider = Create(new GeoLite2Options { Enabled = false }, BuildTime, _ => throw new InvalidOperationException("must not open"));
        Assert.Equal("disabled", provider.Descriptor.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.Equal("disabled", (await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default)).Status);
    }

    [Fact]
    public async Task LicenseFlagDoesNotBlockReadableCityFile()
    {
        using var provider = Create(new GeoLite2Options { Enabled = true, LicenseConfirmed = false, DatabasePath = "controlled-city.mmdb" }, BuildTime,
            _ => Reader("GeoLite2-City", BuildTime));
        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("notFound", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Fact]
    public void MissingConfigurationOrFileRemainsNotReadyWithoutLeakingThePath()
    {
        using var unconfigured = new GeoLite2CityDataProvider(Microsoft.Extensions.Options.Options.Create(new GeoLite2Options { Enabled = true }), new MutableTimeProvider(BuildTime));
        Assert.Equal("notReady", unconfigured.Descriptor.Status);
        using var missing = Create(new GeoLite2Options { Enabled = true, DatabasePath = "controlled-missing.mmdb" }, BuildTime,
            _ => throw new FileNotFoundException("private storage location", "controlled-missing.mmdb"));
        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.DoesNotContain("controlled-missing", missing.Descriptor.Message);
        Assert.DoesNotContain("private storage", missing.Descriptor.Message);
    }

    [Fact]
    public void CountryFileCannotBeActivatedAsTheCitySource()
    {
        using var provider = CreateWithDatabase("GeoLite2-Country", BuildTime, BuildTime.AddHours(1));
        Assert.Equal("unavailable", provider.Descriptor.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.Contains("只接受 GeoLite2-City", provider.Descriptor.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void LegacyAgeSettingDoesNotBlockReadableSource(int days)
    {
        using var provider = Create(new GeoLite2Options { Enabled = true, DatabasePath = "controlled.mmdb", MaxAgeDays = days }, BuildTime,
            _ => Reader("GeoLite2-City", BuildTime));
        Assert.Equal("ready", provider.Descriptor.Status);
        Assert.True(provider.Descriptor.Ready);
    }

    [Fact]
    public async Task DataAgeDoesNotAutomaticallyStopRunningSource()
    {
        var clock = new MutableTimeProvider(BuildTime.AddDays(29));
        using var provider = new GeoLite2CityDataProvider(Microsoft.Extensions.Options.Options.Create(Configured()), clock,
            _ => Reader("GeoLite2-City", BuildTime));
        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("2026-11-01T00:00:00Z", provider.Descriptor.Version);
        clock.UtcNow = BuildTime.AddDays(365);
        Assert.Equal("ready", provider.Descriptor.Status);
        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("notFound", (await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default)).Status);
    }

    [Fact]
    public void OlderDatabaseRemainsUsableAndInvalidFutureBuildIsStillRejected()
    {
        using var expired = CreateWithDatabase("GeoLite2-City", BuildTime, BuildTime.AddDays(31));
        Assert.Equal("ready", expired.Descriptor.Status);
        Assert.True(expired.Descriptor.Ready);
        using var future = CreateWithDatabase("GeoLite2-City", BuildTime, BuildTime.AddDays(-1));
        Assert.Equal("unavailable", future.Descriptor.Status);
        Assert.False(future.Descriptor.Ready);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public async Task OfficialReaderNoMatchIsNotAServiceFailure(string address)
    {
        using var provider = CreateWithDatabase("GeoLite2-City", BuildTime, BuildTime.AddHours(1));
        Assert.True(provider.Descriptor.Ready);
        var result = await provider.QueryAsync(IPAddress.Parse(address), default);
        Assert.Equal("notFound", result.Status);
        Assert.Null(result.Fields.CountryCode);
        Assert.Null(result.Fields.Isp);
        Assert.Null(result.Fields.Asn);
    }

    [Fact]
    public async Task CanceledRequestDoesNotReadOrReturnANormalResult()
    {
        using var provider = CreateWithDatabase("GeoLite2-City", BuildTime, BuildTime.AddHours(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(IPAddress.Parse("1.1.1.1"), cancellation.Token));
    }

    private static GeoLite2Options Configured() => new() { Enabled = true, DatabasePath = "controlled-city.mmdb" };
    private static GeoLite2CityDataProvider Create(GeoLite2Options options, DateTimeOffset now, Func<string, DatabaseReader> factory) =>
        new(Microsoft.Extensions.Options.Options.Create(options), new MutableTimeProvider(now), factory);
    private static GeoLite2CityDataProvider CreateWithDatabase(string type, DateTimeOffset built, DateTimeOffset now) =>
        Create(Configured(), now, _ => Reader(type, built));

    /// <summary>按官方 MMDB 格式生成只含“无匹配”分支的最小夹具，不包含供应商真实记录或个人路径。</summary>
    private static DatabaseReader Reader(string type, DateTimeOffset built)
    {
        var bytes = new List<byte> { 0, 0, 1, 0, 0, 1 }; // 一个节点，两侧均为 node_count，表示无匹配。
        bytes.AddRange(new byte[16]);
        bytes.AddRange([0xab, 0xcd, 0xef]);
        bytes.AddRange(Encoding.ASCII.GetBytes("MaxMind.com"));
        bytes.Add((byte)((7 << 5) | 9));
        Pair("binary_format_major_version", 5, 2);
        Pair("binary_format_minor_version", 5, 0);
        Text("build_epoch"); Integer(9, (ulong)built.ToUnixTimeSeconds());
        Text("database_type"); Text(type);
        Text("description"); bytes.Add((7 << 5) | 1); Text("en"); Text("Controlled empty database");
        Pair("ip_version", 5, 6);
        Pair("node_count", 6, 1);
        Pair("record_size", 5, 24);
        Text("languages"); bytes.Add(2); bytes.Add(4); Text("zh-CN"); Text("en");
        return new DatabaseReader(new MemoryStream(bytes.ToArray(), writable: false), ["zh-CN", "en"]);

        void Pair(string key, int kind, ulong value) { Text(key); Integer(kind, value); }
        void Text(string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            if (encoded.Length < 29) bytes.Add((byte)((2 << 5) | encoded.Length));
            else { bytes.Add((2 << 5) | 29); bytes.Add((byte)(encoded.Length - 29)); }
            bytes.AddRange(encoded);
        }
        void Integer(int kind, ulong value)
        {
            var encoded = new List<byte>();
            while (value > 0) { encoded.Insert(0, (byte)value); value >>= 8; }
            bytes.Add((byte)(((kind <= 7 ? kind : 0) << 5) | encoded.Count));
            if (kind > 7) bytes.Add((byte)(kind - 7));
            bytes.AddRange(encoded);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
