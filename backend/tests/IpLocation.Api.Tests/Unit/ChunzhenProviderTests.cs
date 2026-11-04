using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl.Providers.Chunzhen;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>DAT 夹具完全自造，不复制供应商文件；真实文件只在显式提供路径的只读验证中使用。</summary>
public sealed class ChunzhenProviderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DirectAndBothRedirectModesKeepRawRegionAndNetworkText(int redirect)
    {
        using var database = QqwryDatabase.FromBytes(Fixture(redirect));
        var result = database.Lookup(IPAddress.Parse("8.8.8.8"));
        Assert.NotNull(result);
        Assert.Equal("受控地区原文", result.Region);
        Assert.Equal("受控网络描述", result.Isp);
        Assert.Null(result.Country);
        Assert.Null(result.CountryCode);
        Assert.Null(result.City);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
        Assert.Equal("2026-10-28", database.Version);
        Assert.Null(database.Lookup(IPAddress.Parse("1.1.1.1")));
        Assert.Null(database.Lookup(IPAddress.Parse("8.8.9.1")));
        Assert.Null(database.Lookup(IPAddress.Parse("2001:4860::8888")));
    }

    [Fact]
    public void BadIndexAndRecordBoundsAreRejectedBeforePublishing()
    {
        var bytes = Fixture(0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => QqwryDatabase.FromBytes(bytes));
        bytes = Fixture(0);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bytes[start + 4] = 1; bytes[start + 5] = 0; bytes[start + 6] = 0;
        Assert.Throws<InvalidDataException>(() => QqwryDatabase.FromBytes(bytes));
        Assert.Throws<InvalidDataException>(() => QqwryDatabase.FromBytes(Fixture(0), maxEntries: 1));
    }

    [Fact]
    public void RedirectCycleAndOutOfDataPointerFailWithoutUnboundedReading()
    {
        var bytes = Fixture(1);
        WritePointer(bytes, 13, 12); // 首记录 8 + end(4)，mode1 回到自己。
        using (var database = QqwryDatabase.FromBytes(bytes))
            Assert.Throws<InvalidDataException>(() => database.Lookup(IPAddress.Parse("8.8.8.8")));
        bytes = Fixture(2);
        WritePointer(bytes, 13, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        using var outside = QqwryDatabase.FromBytes(bytes);
        Assert.Throws<InvalidDataException>(() => outside.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void OrderedReservedAddressOverlapIsAcceptedButDuplicateStartsAreRejected()
    {
        var bytes = Fixture(0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue);
        using (var database = QqwryDatabase.FromBytes(bytes))
            Assert.Equal("受控地区原文", database.Lookup(IPAddress.Parse("8.8.8.8"))!.Region);
        var last = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)last), 0x08080800);
        Assert.Throws<InvalidDataException>(() => QqwryDatabase.FromBytes(bytes));
    }

    [Fact]
    public void EmptyStringsAndKnownPlaceholdersRemainNullAndStringsAreBounded()
    {
        using (var database = QqwryDatabase.FromBytes(Fixture(0, region: "", isp: "CZ88.NET")))
            Assert.Null(database.Lookup(IPAddress.Parse("8.8.8.8")));
        using var oversized = QqwryDatabase.FromBytes(Fixture(0, region: new string('x', 4097)));
        Assert.Throws<InvalidDataException>(() => oversized.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void MissingDateUsesContentHashAndNotFileModificationTime()
    {
        var bytes = Fixture(0, date: "受控版本无日期");
        using var database = QqwryDatabase.FromBytes(bytes);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)), database.Version);
    }

    [Fact]
    public async Task ProviderSupportsIpv4MappedAddressesAndRejectsNativeIpv6()
    {
        using var provider = new ChunzhenDataProvider(OptionsFactory.Create(new ChunzhenOptions { Enabled = true, DatabasePath = "controlled" }),
            (_, _, _) => QqwryDatabase.FromBytes(Fixture(2)));
        Assert.True(provider.Descriptor.Ready);
        Assert.False(provider.Descriptor.SupportsIpv6);
        Assert.Equal("success", (await provider.QueryAsync(IPAddress.Parse("::ffff:8.8.8.8"), default)).Status);
        Assert.Equal("unsupported", (await provider.QueryAsync(IPAddress.Parse("2001:4860::8888"), default)).Status);
        using var absent = new ChunzhenDataProvider(OptionsFactory.Create(new ChunzhenOptions { Enabled = true }));
        Assert.True(absent.Descriptor.Enabled);
        Assert.Equal("notReady", absent.Descriptor.Status);
    }

    [Fact]
    public async Task ExplicitRealDatPathIsReadOnlyAndKeepsItsDatePrecision()
    {
        // 正常测试不依赖个人文件；显式环境参数仅选择本轮已获准文件的只读核验，不写入默认配置。
        var path = Environment.GetEnvironmentVariable("IPLOCATION_QQWRY_VALIDATION_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        var before = new FileInfo(path);
        var length = before.Length; var modified = before.LastWriteTimeUtc;
        using var checkedDatabase = QqwryDatabase.Open(path, 268_435_456, 10_000_000);
        using var provider = new ChunzhenDataProvider(OptionsFactory.Create(new ChunzhenOptions { Enabled = true, DatabasePath = path }));
        Assert.True(provider.Descriptor.Ready);
        Assert.Equal("2026-10-28", provider.Descriptor.Version);
        foreach (var ip in new[] { "8.8.8.8", "203.0.113.89" })
        {
            var result = await provider.QueryAsync(IPAddress.Parse(ip), default);
            Assert.Equal("success", result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Fields.Region));
            Assert.Null(result.Fields.CountryCode); Assert.Null(result.Fields.City); Assert.Null(result.Fields.Latitude);
        }
        var after = new FileInfo(path);
        Assert.Equal(length, after.Length); Assert.Equal(modified, after.LastWriteTimeUtc);
    }

    private static byte[] Fixture(int redirect, string region = "受控地区原文", string isp = "受控网络描述", string date = "2026年10月28日IP数据")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(936);
        using var stream = new MemoryStream(); stream.Write(new byte[8]);
        void UInt(uint value) { Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value); stream.Write(data); }
        void Text(string value) { stream.Write(gbk.GetBytes(value)); stream.WriteByte(0); }
        var record = (uint)stream.Position; UInt(0x080808ff);
        if (redirect == 0) { Text(region); Text(isp); }
        else if (redirect == 1)
        {
            stream.WriteByte(1); var pointer = stream.Position; stream.Write(new byte[3]);
            var location = (uint)stream.Position; Text(region); Text(isp);
            var end = stream.Position; stream.Position = pointer; stream.Write([(byte)location, (byte)(location >> 8), (byte)(location >> 16)]); stream.Position = end;
        }
        else
        {
            stream.WriteByte(2); var countryPointer = stream.Position; stream.Write(new byte[3]);
            stream.WriteByte(2); var areaPointer = stream.Position; stream.Write(new byte[3]);
            var country = (uint)stream.Position; Text(region); var area = (uint)stream.Position; Text(isp);
            var end = stream.Position; stream.Position = countryPointer; stream.Write([(byte)country, (byte)(country >> 8), (byte)(country >> 16)]);
            stream.Position = areaPointer; stream.Write([(byte)area, (byte)(area >> 8), (byte)(area >> 16)]); stream.Position = end;
        }
        var metadata = (uint)stream.Position; UInt(uint.MaxValue); Text("纯真网络"); Text(date);
        var indexStart = (uint)stream.Position; UInt(0x08080800); stream.Write([(byte)record, (byte)(record >> 8), (byte)(record >> 16)]);
        var lastIndex = (uint)stream.Position; UInt(0xffffff00); stream.Write([(byte)metadata, (byte)(metadata >> 8), (byte)(metadata >> 16)]);
        stream.Position = 0; UInt(indexStart); UInt(lastIndex);
        return stream.ToArray();
    }
    private static void WritePointer(byte[] data, int offset, uint pointer)
    { data[offset] = (byte)pointer; data[offset + 1] = (byte)(pointer >> 8); data[offset + 2] = (byte)(pointer >> 16); }
}
