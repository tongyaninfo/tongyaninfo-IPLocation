using System.Buffers.Binary;
using System.Net;
using IP2Region.Net.Abstractions;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl.Providers.Ip2Region;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>内存自造 XDB 结构和查询器，测试不读取或复制真实供应商数据库。</summary>
public sealed class Ip2RegionProviderTests
{
    private static readonly DateTimeOffset Built = new(2026, 10, 28, 4, 21, 41, TimeSpan.Zero);
    private const int IndexStart = 256 + 524_288;

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void HeaderVersionFamilyBuildAndIndexBoundsAreReadFromTheFile(int family)
    {
        using var stream = Fixture(family);
        Assert.Equal(Built, Ip2RegionXdbDatabase.ValidateMetadata(stream, family, 1_048_576));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    public void WrongHeaderVersionPolicyFamilyOrPointerSizeIsRejected(int offset)
    {
        using var stream = Fixture(4);
        stream.Position = offset;
        stream.WriteByte(99);
        Assert.Throws<InvalidDataException>(() => Ip2RegionXdbDatabase.ValidateMetadata(stream, 4, 1_048_576));
    }

    [Fact]
    public void InvalidVectorPointersAndFileSizeLimitsCannotPublishAReadyReader()
    {
        using var stream = Fixture(4);
        stream.Position = 256;
        stream.Write([1, 0, 0, 0, 1, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => Ip2RegionXdbDatabase.ValidateMetadata(stream, 4, 1_048_576));
        using var valid = Fixture(4);
        Assert.Throws<InvalidDataException>(() => Ip2RegionXdbDatabase.ValidateMetadata(valid, 4, valid.Length - 1));
        using var truncated = new MemoryStream(new byte[32]);
        Assert.Throws<InvalidDataException>(() => Ip2RegionXdbDatabase.ValidateMetadata(truncated, 4, 1_048_576));
    }

    [Theory]
    [InlineData(4, 14)]
    [InlineData(6, 38)]
    public void OfficialFinalVectorMayUseTheExclusiveEndOfIndexSentinel(int family, int recordBytes)
    {
        using var stream = Fixture(family);
        stream.Position = IndexStart - 8;
        var pointers = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pointers, IndexStart);
        BinaryPrimitives.WriteUInt32LittleEndian(pointers.AsSpan(4), (uint)(IndexStart + recordBytes));
        stream.Write(pointers);
        Assert.Equal(Built, Ip2RegionXdbDatabase.ValidateMetadata(stream, family, 1_048_576));
    }

    [Fact]
    public void V3FieldsUseProvinceCityIspAndCountryCodeWithoutTheOldRegionColumn()
    {
        var fields = Ip2RegionXdbDatabase.ParseRegion("中国|受控省|受控市|受控运营商|CN");
        Assert.NotNull(fields);
        Assert.Equal("中国", fields.Country);
        Assert.Equal("受控省", fields.Region);
        Assert.Equal("受控市", fields.City);
        Assert.Equal("受控运营商", fields.Isp);
        Assert.Equal("CN", fields.CountryCode);
        Assert.Null(fields.Asn);
        Assert.Null(fields.Latitude);
        Assert.Null(fields.Longitude);
        Assert.Null(fields.PostalCode);
        Assert.Null(fields.TimeZone);
    }

    [Fact]
    public void EmptyAndZeroPlaceholdersRemainNull()
    {
        Assert.Null(Ip2RegionXdbDatabase.ParseRegion("0|0|0|0|0"));
        Assert.Null(Ip2RegionXdbDatabase.ParseRegion(""));
        var fields = Ip2RegionXdbDatabase.ParseRegion("United States|0||Google LLC|US");
        Assert.NotNull(fields);
        Assert.Null(fields.Region);
        Assert.Null(fields.City);
        Assert.Equal("Google LLC", fields.Isp);
    }

    [Theory]
    [InlineData("China|Old area|Province|City|ISP|CN")]
    [InlineData("中国|省|市|ISP|invalid")]
    [InlineData("中国|省|bad\u0001city|ISP|CN")]
    public void UnexpectedFieldsAndUnsafeTextCannotBecomeAValidResult(string text) =>
        Assert.Throws<InvalidDataException>(() => Ip2RegionXdbDatabase.ParseRegion(text));

    [Fact]
    public void DisabledAndMissingConfigurationNeverOpenAFile()
    {
        Ip2RegionXdbDatabase Factory(string path, int family, long maximum) => throw new InvalidOperationException("must not open");
        using var disabled = new Ip2RegionDataProvider(OptionsFactory.Create(new Ip2RegionOptions()), Factory);
        using var absent = new Ip2RegionDataProvider(OptionsFactory.Create(new Ip2RegionOptions { Enabled = true, LicenseConfirmed = true }), Factory);
        Assert.Equal("disabled", disabled.Descriptor.Status);
        Assert.Equal("notReady", absent.Descriptor.Status);
    }

    [Fact]
    public async Task LegacyLicenseFlagDoesNotBlockValidXdbQueries()
    {
        var options = Configured();
        options.LicenseConfirmed = false;
        using var provider = new Ip2RegionDataProvider(OptionsFactory.Create(options), (_, family, _) => new(family, Built,
            new ControlledSearcher(_ => "Country|Region|City|ISP|US")));
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.LicenseConfirmed);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Fact]
    public async Task FamiliesUseIndependentReadersAndActualBuildVersions()
    {
        using var provider = Create(ipv6: true);
        Assert.True(provider.Descriptor.SupportsIpv4);
        Assert.True(provider.Descriptor.SupportsIpv6);
        var ipv4 = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        var mapped = await provider.QueryAsync(IPAddress.Parse("::ffff:8.8.8.8"), default);
        var ipv6 = await provider.QueryAsync(IPAddress.Parse("2001:4860:4860::8888"), default);
        Assert.Equal("v4 city", ipv4.Fields.City);
        Assert.Equal(ipv4.Fields, mapped.Fields);
        Assert.Equal("2026-10-28T04:21:41Z", ipv4.Version);
        Assert.Equal("v6 city", ipv6.Fields.City);
        Assert.Equal("2026-08-14T05:38:41Z", ipv6.Version);
        Assert.Contains("v4=", provider.Descriptor.Version);
        Assert.Contains("v6=", provider.Descriptor.Version);
    }

    [Fact]
    public async Task AbsentFamilyAndEmptyLookupAreExplicitStates()
    {
        using var provider = Create();
        Assert.False(provider.Descriptor.SupportsIpv6);
        Assert.Equal("unsupported", (await provider.QueryAsync(IPAddress.Parse("2001:4860:4860::8888"), default)).Status);
        using var empty = Create(searcher: _ => new ControlledSearcher(_ => ""));
        Assert.Equal("notFound", (await empty.QueryAsync(IPAddress.Parse("8.8.8.8"), default)).Status);
    }

    [Fact]
    public async Task SharedFilePositionIsProtectedDuringConcurrentQueriesAndCancellationSkipsTheReader()
    {
        var active = 0;
        var maximum = 0;
        var count = 0;
        using var database = new Ip2RegionXdbDatabase(4, Built, new ControlledSearcher(address =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            Interlocked.Increment(ref count);
            Thread.Sleep(1);
            Interlocked.Decrement(ref active);
            return "Country|Region|" + address + "|0|US";
        }));
        var results = await Task.WhenAll(Enumerable.Range(1, 32).Select(index => Task.Run(async () =>
            (Index: index, Fields: await database.LookupAsync(IPAddress.Parse("8.8.8." + index), default)))));
        Assert.Equal(1, maximum);
        Assert.All(results, result => Assert.Equal("8.8.8." + result.Index, result.Fields?.City));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.LookupAsync(IPAddress.Parse("8.8.8.8"), cancellation.Token));
        Assert.Equal(32, count);
    }

    [Fact]
    public async Task CorruptResultMarksSourceUnavailableWithoutLeakingPaths()
    {
        using var provider = Create(searcher: _ => new ControlledSearcher(_ => throw new IOException("private /controlled-path/database.xdb")));
        var result = await provider.QueryAsync(IPAddress.Parse("8.8.8.8"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.DoesNotContain("controlled-path", result.Message);
    }

    [Fact]
    public void MissingOrInvalidConfiguredFileDoesNotPublishTheOtherFamilyAsFullyReady()
    {
        using var missing = new Ip2RegionDataProvider(OptionsFactory.Create(Configured(ipv6: true)), (path, family, maximum) =>
            family == 4 ? new(4, Built, new ControlledSearcher(_ => "Country|Region|City|0|US")) : throw new FileNotFoundException("private /path"));
        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.False(missing.Descriptor.Ready);
        using var invalid = new Ip2RegionDataProvider(OptionsFactory.Create(Configured()), (_, _, _) => throw new InvalidDataException("private /path"));
        Assert.Equal("unavailable", invalid.Descriptor.Status);
        Assert.DoesNotContain("/path", invalid.Descriptor.Message);
    }

    private static Ip2RegionOptions Configured(bool ipv6 = false) => new() { Enabled = true, LicenseConfirmed = true, Ipv4DatabasePath = "controlled-v4.xdb", Ipv6DatabasePath = ipv6 ? "controlled-v6.xdb" : "" };
    private static Ip2RegionDataProvider Create(bool ipv6 = false, Func<int, ISearcher>? searcher = null) =>
        new(OptionsFactory.Create(Configured(ipv6)), (_, family, _) => new(family, family == 4 ? Built : new(2026, 8, 14, 5, 38, 41, TimeSpan.Zero),
            searcher?.Invoke(family) ?? new ControlledSearcher(_ => "Country|Region|v" + family + " city|ISP|US")));

    private static MemoryStream Fixture(int family)
    {
        var recordBytes = family == 4 ? 14 : 38;
        var data = new byte[IndexStart + recordBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)Built.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), IndexStart);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), IndexStart);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), (ushort)family);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 4);
        return new MemoryStream(data, writable: true);
    }

    private sealed class ControlledSearcher(Func<IPAddress, string> lookup) : ISearcher
    {
        public int IoCount => 0;
        public string? Search(string ipStr) => lookup(IPAddress.Parse(ipStr));
        public string? Search(IPAddress ipAddress) => lookup(ipAddress);
        [Obsolete]
        public string? Search(uint ipAddress) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
