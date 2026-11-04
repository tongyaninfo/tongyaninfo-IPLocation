using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl.Providers.Ip2Location;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>全部数据在内存中自造，不读取、复制或清理用户提供的真实数据库。</summary>
public sealed class Ip2LocationProviderTests
{
    [Theory]
    [InlineData("\0")]
    [InlineData("\u0001")]
    [InlineData("\u001f")]
    public void InvalidTextControlCharactersCannotReachXmlOutput(string control) =>
        Assert.Throws<InvalidDataException>(() => Load(Row("1", "2", city: "bad" + control + "city")));

    [Fact]
    public void QuotedCommaChineseMultilineAndEscapedQuotesPreserveByteOffsets()
    {
        var csv = Row("10", "20", country: "中国,测试", region: "测试省\r\n新区", city: "测试\"市") +
                  Row("21", "30", countryCode: "US", country: "United States", city: "next");
        using var database = Load(csv);

        var first = Assert.IsType<IpLocation.Api.Models.Responses.IpFields>(database.Lookup(10));
        Assert.Equal("中国,测试", first.Country);
        Assert.Equal("测试省\r\n新区", first.Region);
        Assert.Equal("测试\"市", first.City);
        var second = Assert.IsType<IpLocation.Api.Models.Responses.IpFields>(database.Lookup(30));
        Assert.Equal("US", second.CountryCode);
        Assert.Equal("next", second.City);
    }

    [Fact]
    public void Utf8BomAndCrLfAreAcceptedWithoutBecomingPartOfTheFirstNumber()
    {
        using var database = Load("\uFEFF" + Row("1", "2"));

        Assert.Equal("CN", database.Lookup(1)?.CountryCode);
        Assert.Equal(1, database.EntryCount);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(200u)]
    [InlineData(150u)]
    public void LookupIncludesBothRangeBoundaries(uint address)
    {
        using var database = Load(Row("100", "200"));

        Assert.Equal("CN", database.Lookup(address)?.CountryCode);
    }

    [Theory]
    [InlineData(99u)]
    [InlineData(201u)]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void AddressOutsideCoveredRangesIsNotFound(uint address)
    {
        using var database = Load(Row("100", "200"));

        Assert.Null(database.Lookup(address));
    }

    [Fact]
    public void LowestAndHighestIpv4ValuesCanBeIndexedWithoutIntegerOverflow()
    {
        using var database = Load(Row("0", "0", city: "first") + Row("4294967295", "4294967295", city: "last"));

        Assert.Equal("first", database.Lookup(0)?.City);
        Assert.Equal("last", database.Lookup(uint.MaxValue)?.City);
        Assert.Equal(0u, database.MinStart);
        Assert.Equal(uint.MaxValue, database.MaxEnd);
    }

    [Fact]
    public void GapBetweenAdjacentIndexEntriesDoesNotBorrowAnotherRange()
    {
        using var database = Load(Row("10", "20") + Row("30", "40"));

        Assert.Null(database.Lookup(21));
        Assert.Null(database.Lookup(29));
        Assert.NotNull(database.Lookup(30));
    }

    [Theory]
    [InlineData("10", "20", "0", "9")]
    [InlineData("0", "10", "10", "20")]
    [InlineData("0", "10", "0", "20")]
    public void UnsortedOrOverlappingRangesCannotProduceAReadyIndex(string from1, string to1, string from2, string to2)
    {
        Assert.Throws<InvalidDataException>(() => Load(Row(from1, to1) + Row(from2, to2)));
    }

    [Theory]
    [InlineData("10", "9")]
    [InlineData("-1", "10")]
    [InlineData("0", "4294967296")]
    [InlineData("4294967296", "4294967296")]
    [InlineData("not-an-integer", "10")]
    public void InvalidOrOutOfIpv4BoundsRangesAreRejected(string from, string to)
    {
        Assert.Throws<InvalidDataException>(() => Load(Row(from, to)));
    }

    [Theory]
    [InlineData("91", "0")]
    [InlineData("0", "181")]
    [InlineData("NaN", "0")]
    [InlineData("0", "Infinity")]
    public void InvalidCoordinatesAreNotSilentlyConvertedToAUsableLocation(string latitude, string longitude)
    {
        Assert.Throws<InvalidDataException>(() => Load(Row("1", "2", latitude: latitude, longitude: longitude)));
    }

    [Fact]
    public void MissingFieldsRemainNullAndDb11CannotInventIspOrAsn()
    {
        using var database = Load(Row("1", "2", region: "-", city: " ", latitude: "-", longitude: "-", postal: "-", timeZone: "-"));

        var result = Assert.IsType<IpLocation.Api.Models.Responses.IpFields>(database.Lookup(1));
        Assert.Equal("CN", result.CountryCode);
        Assert.Null(result.Region);
        Assert.Null(result.City);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
        Assert.Null(result.PostalCode);
        Assert.Null(result.TimeZone);
        Assert.Null(result.Isp);
        Assert.Null(result.Asn);
        Assert.Null(result.AsName);
    }

    [Fact]
    public void ValidZeroCoordinatesAndPostalLeadingZerosArePreserved()
    {
        using var database = Load(Row("1", "2", latitude: "0", longitude: "0", postal: "00001", timeZone: "+00:00"));

        var result = Assert.IsType<IpLocation.Api.Models.Responses.IpFields>(database.Lookup(1));
        Assert.Equal(0d, result.Latitude);
        Assert.Equal(0d, result.Longitude);
        Assert.Equal("00001", result.PostalCode);
        Assert.Equal("+00:00", result.TimeZone);
    }

    [Fact]
    public void EmptyCountryAndZeroPlaceholdersDoNotCreateAFalseLocation()
    {
        using var database = Load(Row("1", "2", countryCode: "-", country: "-", region: "-", city: "-",
            latitude: "0.000000", longitude: "0.000000", postal: "-", timeZone: "-"));

        Assert.Null(database.Lookup(1));
    }

    [Fact]
    public void WrongFieldCountAndUnterminatedQuotesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Load(Record("1", "2", "CN", "中国", "省", "市", "0", "0", "-")));
        Assert.Throws<InvalidDataException>(() => Load(Record("1", "2", "CN", "中国", "省", "市", "0", "0", "-", "+08:00", "extra")));
        Assert.Throws<InvalidDataException>(() => Load("\"1\",\"2\",\"CN\",\"China\",\"Region\",\"City\",\"0\",\"0\",\"-\",\"+08:00"));
    }

    [Fact]
    public void CorruptUtf8IsRejectedInsteadOfReturningReplacementCharacters()
    {
        var bytes = Encoding.ASCII.GetBytes("\"1\",\"2\",\"CN\",\"")
            .Concat(new byte[] { 0xff })
            .Concat(Encoding.ASCII.GetBytes("\",\"-\",\"-\",\"0\",\"0\",\"-\",\"-\"\r\n")).ToArray();
        using var stream = new MemoryStream(bytes, writable: false);

        Assert.Throws<InvalidDataException>(() => Ip2LocationCsvDatabase.Load(stream));
    }

    [Fact]
    public void IndexAndRecordResourceLimitsAreEnforced()
    {
        using var tooMany = Bytes(Row("1", "2") + Row("3", "4"));
        Assert.Throws<InvalidDataException>(() => Ip2LocationCsvDatabase.Load(tooMany, maxEntries: 1));
        using var tooLarge = Bytes(Row("1", "2", city: new string('x', 5_000)));
        Assert.Throws<InvalidDataException>(() => Ip2LocationCsvDatabase.Load(tooLarge, maxRecordBytes: 512));
    }

    [Fact]
    public void FingerprintCoversTheOriginalBytesWithoutBecomingAnOfficialReleaseDate()
    {
        var bytes = Encoding.UTF8.GetBytes(Row("1", "2"));
        using var database = Ip2LocationCsvDatabase.Load(new MemoryStream(bytes, writable: false));

        Assert.Equal(bytes.LongLength, database.FileLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), database.Sha256.ToLowerInvariant());
    }

    [Fact]
    public async Task ParallelLookupsCannotCorruptTheSharedStreamPosition()
    {
        const uint start = 16_843_008; // 1.1.1.0
        var csv = string.Concat(Enumerable.Range(0, 128).Select(index =>
            Row((start + (uint)index).ToString(CultureInfo.InvariantCulture),
                (start + (uint)index).ToString(CultureInfo.InvariantCulture), city: "城市" + index)));
        using var database = Load(csv);

        var results = await Task.WhenAll(Enumerable.Range(0, 128).Select(index => Task.Run(() =>
            (Index: index, Fields: database.Lookup(start + (uint)index)))));

        Assert.All(results, result => Assert.Equal("城市" + result.Index, result.Fields?.City));
    }

    [Fact]
    public void PreCanceledLoadDoesNotReadTheStreamAndLookupHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new CountingReadStream(Encoding.UTF8.GetBytes(Row("1", "2")));

        Assert.ThrowsAny<OperationCanceledException>(() => Ip2LocationCsvDatabase.Load(stream, cancellation.Token));
        Assert.Equal(0, stream.ReadCalls);
        using var database = Load(Row("1", "2"));
        Assert.ThrowsAny<OperationCanceledException>(() => database.Lookup(1, cancellation.Token));
    }

    [Fact]
    public void CancellationRaisedDuringLoadCannotPublishAPartialIndex()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CountingReadStream(Encoding.UTF8.GetBytes(Row("1", "2") + Row("3", "4")), cancellation);

        Assert.ThrowsAny<OperationCanceledException>(() => Ip2LocationCsvDatabase.Load(stream, cancellation.Token));
        Assert.True(stream.ReadCalls > 0);
    }

    [Fact]
    public async Task DisabledSourceNeverCallsTheDatabaseFactory()
    {
        var opens = 0;
        var options = Configured();
        options.Enabled = false;
        using var provider = Create(options, (_, _) =>
        {
            opens++;
            throw new InvalidOperationException("must not open");
        });

        Assert.Equal("disabled", provider.Descriptor.Status);
        Assert.Equal("disabled", (await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default)).Status);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void UnsetPathNeverReadsAFile()
    {
        var opens = 0;
        Ip2LocationCsvDatabase Factory(string path, CancellationToken cancellationToken)
        {
            opens++;
            throw new InvalidOperationException("must not open");
        }
        var unset = Configured();
        unset.DatabasePath = "";
        using var missing = Create(unset, Factory);

        Assert.Equal("notReady", missing.Descriptor.Status);
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task LegacyLicenseFlagDoesNotBlockValidCsvQueries()
    {
        var options = Configured();
        options.LicenseConfirmed = false;
        using var provider = Create(options);
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.LicenseConfirmed);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default)).Status);
    }

    [Fact]
    public async Task ReaderErrorsDoNotExposeTheLocalPath()
    {
        var options = Configured();
        options.DatabasePath = "/controlled-private/fixture.csv";
        using var missing = Create(options, (_, _) => throw new FileNotFoundException("private location", options.DatabasePath));
        using var invalid = Create(options, (_, _) => throw new InvalidDataException("private location /controlled-private/fixture.csv"));

        Assert.False(missing.Descriptor.Ready);
        Assert.False(invalid.Descriptor.Ready);
        Assert.DoesNotContain("controlled-private", missing.Descriptor.Message);
        Assert.DoesNotContain("private location", invalid.Descriptor.Message);
        var result = await invalid.QueryAsync(IPAddress.Parse("1.1.1.1"), default);
        Assert.Equal("unavailable", result.Status);
        Assert.DoesNotContain("controlled-private", result.Message);
    }

    [Fact]
    public async Task ChangedRecordCannotReturnStaleIndexDataAndMarksSourceUnavailable()
    {
        var bytes = Encoding.UTF8.GetBytes(Row("16843009", "16843009"));
        using var provider = Create(factory: (_, cancellationToken) =>
            Ip2LocationCsvDatabase.Load(new MemoryStream(bytes, writable: true), cancellationToken));
        Assert.True(provider.Descriptor.Ready);

        // 只改变内存夹具，模拟索引建立后原始记录变动；生产文件及磁盘完全不参与。
        bytes[1] = (byte)'2';
        var result = await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default);

        Assert.Equal("unavailable", result.Status);
        Assert.False(provider.Descriptor.Ready);
        Assert.Null(result.Fields.CountryCode);
    }

    [Fact]
    public async Task ReadyIpv4SourceReturnsRealFieldsAndExplicitIpv6Unsupported()
    {
        using var provider = Create();
        Assert.True(provider.Descriptor.Ready);
        Assert.True(provider.Descriptor.SupportsIpv4);
        Assert.False(provider.Descriptor.SupportsIpv6);
        Assert.Equal("ip2location", provider.Descriptor.Id);
        Assert.Null(provider.Descriptor.Version);

        var hit = await provider.QueryAsync(IPAddress.Parse("1.1.1.1"), default);
        Assert.Equal("success", hit.Status);
        Assert.Equal("CN", hit.Fields.CountryCode);
        Assert.Equal("+08:00", hit.Fields.TimeZone);
        var absent = await provider.QueryAsync(IPAddress.Parse("2.2.2.2"), default);
        Assert.Equal("notFound", absent.Status);
        var ipv6 = await provider.QueryAsync(IPAddress.Parse("2606:4700:4700::1111"), default);
        Assert.Equal("unsupported", ipv6.Status);
        Assert.Null(ipv6.Fields.CountryCode);
    }

    [Fact]
    public void DataVersionIsExplicitAndContentHashRequiresOptIn()
    {
        var versioned = Configured();
        versioned.DataVersion = "controlled-version";
        using var known = Create(versioned);
        Assert.Equal("controlled-version", known.Descriptor.Version);
        var hashed = Configured();
        hashed.UseContentHashAsVersion = true;
        using var fingerprint = Create(hashed);
        Assert.NotNull(fingerprint.Descriptor.Version);
        Assert.StartsWith("sha256:", fingerprint.Descriptor.Version!);
        Assert.Equal(71, fingerprint.Descriptor.Version?.Length);
    }

    [Fact]
    public async Task PreCanceledProviderQueryDoesNotPerformAnotherRead()
    {
        var bytes = Encoding.UTF8.GetBytes(Row("16843009", "16843009"));
        var stream = new CountingReadStream(bytes);
        using var provider = Create(factory: (_, cancellationToken) => Ip2LocationCsvDatabase.Load(stream, cancellationToken));
        var reads = stream.ReadCalls;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.QueryAsync(IPAddress.Parse("1.1.1.1"), cancellation.Token));
        Assert.Equal(reads, stream.ReadCalls);
    }

    private static Ip2LocationOptions Configured() => new()
    {
        Enabled = true,
        LicenseConfirmed = true,
        DatabasePath = "controlled.csv"
    };

    private static Ip2LocationCsvDataProvider Create(Ip2LocationOptions? options = null,
        Func<string, CancellationToken, Ip2LocationCsvDatabase>? factory = null) =>
        new(OptionsFactory.Create(options ?? Configured()), factory ?? ((_, cancellationToken) =>
            Ip2LocationCsvDatabase.Load(Bytes(Row("16843009", "16843009")), cancellationToken)));

    private static Ip2LocationCsvDatabase Load(string csv) => Ip2LocationCsvDatabase.Load(Bytes(csv));
    private static MemoryStream Bytes(string value) => new(Encoding.UTF8.GetBytes(value), writable: false);

    private static string Row(string from, string to, string countryCode = "CN", string country = "中国",
        string region = "测试省", string city = "测试市", string latitude = "0", string longitude = "0",
        string postal = "-", string timeZone = "+08:00") =>
        Record(from, to, countryCode, country, region, city, latitude, longitude, postal, timeZone);

    private static string Record(params string[] fields) =>
        string.Join(',', fields.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")) + "\r\n";

    private sealed class CountingReadStream(byte[] bytes, CancellationTokenSource? cancelAfterRead = null)
        : MemoryStream(bytes, writable: false)
    {
        public int ReadCalls { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var read = base.Read(buffer, offset, count);
            cancelAfterRead?.Cancel();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var read = base.Read(buffer);
            cancelAfterRead?.Cancel();
            return read;
        }

        public override int ReadByte()
        {
            ReadCalls++;
            var read = base.ReadByte();
            cancelAfterRead?.Cancel();
            return read;
        }
    }
}
