using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.DbIp;

/// <summary>复用已有 MaxMind SDK 读取 DB-IP City Lite MMDB，不复制或修改原库。</summary>
public sealed class DbIpCityDataProvider : IIpDataProvider, IDisposable
{
    private readonly ProviderDescriptor _descriptor;
    private readonly DatabaseReader? _reader;
    private int _disposed;

    public DbIpCityDataProvider(IOptions<DbIpOptions> options)
        : this(options, path => new DatabaseReader(path, ["zh-CN", "en"], FileAccessMode.MemoryMapped)) { }

    internal DbIpCityDataProvider(IOptions<DbIpOptions> options, Func<string, DatabaseReader> openReader)
    {
        var configuration = options.Value;
        _descriptor = new("dbip", "DB-IP City Lite", "notReady", true, true, configuration.Enabled,
            configuration.LicenseConfirmed, false, null, "尚未提供并验证 DB-IP City Lite MMDB 文件。",
            "https://db-ip.com/db/lite.php", "IP Geolocation by DB-IP (https://db-ip.com)");
        if (!configuration.Enabled)
        {
            _descriptor = _descriptor with { Status = "disabled", Message = "DB-IP City Lite 来源已停用。" };
            return;
        }
        // 授权由人工处理；兼容字段不参与技术读取资格判断。
        if (string.IsNullOrWhiteSpace(configuration.DatabasePath)) return;
        DatabaseReader? opened = null;
        try
        {
            opened = openReader(configuration.DatabasePath);
            if (opened.Metadata.DatabaseType != "DBIP-City-Lite" || opened.Metadata.IPVersion is not (4 or 6))
                throw new InvalidDataException("Unsupported DB-IP database metadata.");
            var built = new DateTimeOffset(DateTime.SpecifyKind(opened.Metadata.BuildDate, DateTimeKind.Utc));
            _descriptor = _descriptor with
            {
                Status = "ready", Ready = true, SupportsIpv6 = opened.Metadata.IPVersion == 6,
                Version = built.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Message = "DB-IP City Lite 只读 MMDB 已就绪；构建时间来自文件 metadata。"
            };
            _reader = opened;
            opened = null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _descriptor = _descriptor with { Message = "DB-IP City Lite 文件不存在或尚未配置。" };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidDatabaseException or ArgumentException or NotSupportedException)
        {
            _descriptor = _descriptor with { Status = "unavailable", Message = "DB-IP City Lite 文件无法读取或校验失败。" };
        }
        finally { opened?.Dispose(); }
    }

    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) == 0 ? _descriptor
        : _descriptor with { Status = "unavailable", Ready = false, Message = "DB-IP City Lite 读取器已停止。" };

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _reader is null)
            return Task.FromResult(new ProviderQueryResult(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !descriptor.SupportsIpv6)
            return Task.FromResult(new ProviderQueryResult("unsupported", new IpFields(), "该 DB-IP 文件不支持 IPv6。", descriptor.Version));
        try
        {
            var result = _reader.City(address);
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new IpFields
            {
                CountryCode = Present(result.Country.IsoCode),
                Country = Name(result.Country.Name, result.Country.Names.Values),
                Region = Name(result.MostSpecificSubdivision.Name, result.MostSpecificSubdivision.Names.Values),
                City = Name(result.City.Name, result.City.Names.Values),
                Latitude = result.Location.Latitude,
                Longitude = result.Location.Longitude
                // City Lite 没有 ISP、ASN、邮编或时区，不借用其他来源补造。
            };
            return Task.FromResult(new ProviderQueryResult("success", fields, "查询完成；地理位置为数据库推断。", descriptor.Version));
        }
        catch (AddressNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderQueryResult("notFound", new IpFields(), "DB-IP City Lite 没有此地址的匹配记录。", descriptor.Version));
        }
        catch (Exception exception) when (exception is GeoIP2Exception or InvalidDatabaseException or IOException or ArgumentException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderQueryResult("unavailable", new IpFields(), "DB-IP City Lite 暂时无法完成查询。", descriptor.Version));
        }
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _reader?.Dispose(); }
    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string? Name(string? preferred, IEnumerable<string> originalNames) => Present(preferred) ?? originalNames.Select(Present).FirstOrDefault(value => value is not null);
}
