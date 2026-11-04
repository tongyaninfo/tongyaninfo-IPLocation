using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.Ip2Location;

/// <summary>复用已有 MaxMind SDK 只读查询 IP2Location DB11 MMDB，不扫描 CSV 或生成索引文件。</summary>
public sealed class Ip2LocationMmdbDataProvider : IIpDataProvider, IDisposable
{
    private readonly ProviderDescriptor _descriptor;
    private readonly DatabaseReader? _reader;
    private int _disposed;
    private int _faulted;

    public Ip2LocationMmdbDataProvider(IOptions<Ip2LocationOptions> options)
        : this(options, path => new DatabaseReader(path, ["zh-CN", "en"], FileAccessMode.MemoryMapped)) { }

    internal Ip2LocationMmdbDataProvider(IOptions<Ip2LocationOptions> options, Func<string, DatabaseReader> openReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openReader);
        var configuration = options.Value;
        _descriptor = new("ip2location", "IP2Location LITE DB11", "notReady", true, false,
            configuration.Enabled, configuration.LicenseConfirmed, false, null,
            "尚未提供并验证 IP2Location DB11 IPv4 MMDB 文件。", "https://www.ip2location.com/lite-terms",
            "通研信息 uses the IP2Location LITE database for IP geolocation (https://lite.ip2location.com).");
        if (!configuration.Enabled)
        {
            _descriptor = _descriptor with { Status = "disabled", Message = "IP2Location LITE 来源已停用。" };
            return;
        }
        // 授权由人工处理；兼容字段不参与技术读取资格判断。
        if (string.IsNullOrWhiteSpace(configuration.DatabasePath)) return;

        DatabaseReader? opened = null;
        try
        {
            opened = openReader(configuration.DatabasePath);
            // 此下载包采用通用 City schema；类型只能校验格式，供应商身份仍由受控配置与文件清单确认。
            if (opened.Metadata.DatabaseType != "GeoLite2-City" || opened.Metadata.IPVersion is not (4 or 6))
                throw new InvalidDataException("Unsupported IP2Location DB11 MMDB metadata.");
            var built = new DateTimeOffset(DateTime.SpecifyKind(opened.Metadata.BuildDate, DateTimeKind.Utc));
            _descriptor = _descriptor with
            {
                Status = "ready", Ready = true,
                Version = built.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                // DB11 IPv4 下载包使用 IPv6 格式树存储 IPv4；ip_version=6 不能证明覆盖 IPv6。
                Message = "DB11 IPv4 只读 MMDB 已就绪；版本为文件 metadata 构建时间，名称优先使用库内中文。"
            };
            _reader = opened;
            opened = null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _descriptor = _descriptor with { Message = "IP2Location MMDB 文件不存在或尚未配置。" };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidDatabaseException or ArgumentException or NotSupportedException or OverflowException)
        {
            // SDK 异常可能含私有路径或文件内容，公开信息仅提供固定错误。
            _descriptor = _descriptor with { Status = "unavailable", Message = "IP2Location DB11 MMDB 无法读取或未通过格式校验。" };
        }
        finally { opened?.Dispose(); }
    }

    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
        ? _descriptor with { Status = "unavailable", Ready = false, Message = "IP2Location 只读数据库当前不可用。" }
        : _descriptor;

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _reader is null)
            return Task.FromResult(new ProviderQueryResult(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return Task.FromResult(new ProviderQueryResult("unsupported", new IpFields(), "当前 DB11 MMDB 下载包仅覆盖 IPv4，不支持 IPv6。", descriptor.Version));
        try
        {
            var result = _reader.City(address);
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new IpFields
            {
                CountryCode = Present(result.Country.IsoCode),
                Country = Name(result.Country.Name, result.Country.Names.Values),
                ContinentCode = Present(result.Continent.Code),
                Continent = Name(result.Continent.Name, result.Continent.Names.Values),
                Region = Name(result.MostSpecificSubdivision.Name, result.MostSpecificSubdivision.Names.Values),
                City = Name(result.City.Name, result.City.Names.Values),
                Latitude = Coordinate(result.Location.Latitude, 90),
                Longitude = Coordinate(result.Location.Longitude, 180),
                PostalCode = Present(result.Postal.Code),
                TimeZone = Present(result.Location.TimeZone)
                // 城市可能只有英文，保留原文；RegisteredCountry 不兜底，DB11 不补造 ISP/ASN。
            };
            var hasPlace = fields.CountryCode is not null || fields.Country is not null || fields.ContinentCode is not null ||
                           fields.Continent is not null || fields.Region is not null || fields.City is not null || fields.PostalCode is not null;
            // 与旧来源一致：没有任何地理文字时，成对零坐标视为占位；真实位置的零坐标仍保留。
            if (!hasPlace && fields.Latitude == 0 && fields.Longitude == 0)
                fields = fields with { Latitude = null, Longitude = null };
            if (!hasPlace && fields.Latitude is null && fields.Longitude is null && fields.TimeZone is null)
                return Task.FromResult(new ProviderQueryResult("notFound", new IpFields(), "DB11 没有该地址的可用定位记录。", descriptor.Version));
            return Task.FromResult(new ProviderQueryResult("success", fields, "查询完成；地理位置为数据库推断，时区保留 MMDB 原值。", descriptor.Version));
        }
        catch (AddressNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderQueryResult("notFound", new IpFields(), "DB11 没有该地址的可用定位记录。", descriptor.Version));
        }
        catch (Exception exception) when (exception is GeoIP2Exception or InvalidDatabaseException or DeserializationException or
            IOException or InvalidDataException or ArgumentException or ObjectDisposedException or XmlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _faulted, 1);
            return Task.FromResult(new ProviderQueryResult("unavailable", new IpFields(), "IP2Location 只读数据暂时无法完成查询。", descriptor.Version));
        }
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _reader?.Dispose(); }
    private static string? Present(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-") return null;
        if (value.Any(char.IsControl)) throw new InvalidDataException("Unsafe IP2Location text field.");
        return XmlConvert.VerifyXmlChars(value);
    }

    private static double? Coordinate(double? value, int limit)
    {
        if (value.HasValue && (!double.IsFinite(value.Value) || Math.Abs(value.Value) > limit))
            throw new InvalidDataException("Invalid IP2Location coordinate.");
        return value;
    }

    private static string? Name(string? preferred, IEnumerable<string> originalNames) =>
        Present(preferred) ?? originalNames.Select(Present).FirstOrDefault(value => value is not null);
}
