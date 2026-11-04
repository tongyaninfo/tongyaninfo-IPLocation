using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Options;

[assembly: InternalsVisibleTo("IpLocation.Api.Tests")]

namespace IpLocation.Api.Service.impl.Providers.GeoLite2;

/// <summary>复用官方只读读取器；每个单例持有一个 City 文件，不下载、不覆盖或清理数据。</summary>
public sealed class GeoLite2CityDataProvider : IIpDataProvider, IDisposable
{
    private static readonly string[] Locales = ["zh-CN", "en"];
    private const string LicenseUrl = "https://www.maxmind.com/en/geolite/eula";
    private const string Attribution = "This product includes GeoLite Data created by MaxMind, available from https://www.maxmind.com.";
    private readonly TimeProvider _timeProvider;
    private readonly ProviderDescriptor _initialDescriptor;
    private readonly DateTimeOffset? _buildUtc;
    private readonly DatabaseReader? _reader;
    private int _disposed;

    public GeoLite2CityDataProvider(IOptions<GeoLite2Options> options, TimeProvider timeProvider)
        : this(options, timeProvider, path => new DatabaseReader(path, Locales, FileAccessMode.MemoryMapped))
    {
    }

    /// <summary>测试可提供内存中的受控 MMDB；生产仍只使用官方文件读取器，不设置运行时测试开关。</summary>
    internal GeoLite2CityDataProvider(IOptions<GeoLite2Options> options, TimeProvider timeProvider,
        Func<string, DatabaseReader> openReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(openReader);
        _timeProvider = timeProvider;
        var configuration = options.Value;
        _initialDescriptor = new("geolite2", "MaxMind GeoLite2 City", "notReady", true, true,
            configuration.Enabled, configuration.LicenseConfirmed, false, null,
            "尚未提供并验证 GeoLite2 City 数据文件。", LicenseUrl, Attribution);

        if (!configuration.Enabled)
        {
            _initialDescriptor = _initialDescriptor with { Status = "disabled", Message = "GeoLite2 City 来源已停用。" };
            return;
        }
        if (string.IsNullOrWhiteSpace(configuration.DatabasePath)) return;

        DatabaseReader? opened = null;
        try
        {
            opened = openReader(configuration.DatabasePath);
            if (!string.Equals(opened.Metadata.DatabaseType, "GeoLite2-City", StringComparison.Ordinal))
            {
                _initialDescriptor = _initialDescriptor with { Status = "unavailable", Message = "此来源只接受 GeoLite2-City 文件，不接受 Country 或其他类型。" };
                return;
            }

            // BuildDate 来自 MMDB metadata，不使用文件 mtime 冒充供应商构建时间。
            _buildUtc = new DateTimeOffset(DateTime.SpecifyKind(opened.Metadata.BuildDate, DateTimeKind.Utc));
            _initialDescriptor = _initialDescriptor with
            {
                SupportsIpv6 = opened.Metadata.IPVersion == 6,
                Version = _buildUtc.Value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            };
            if (_buildUtc > _timeProvider.GetUtcNow())
            {
                _initialDescriptor = _initialDescriptor with { Status = "unavailable", Message = "GeoLite2 数据构建时间超出当前时间，文件尚未通过验证。" };
                return;
            }

            _reader = opened;
            opened = null;
            _initialDescriptor = _initialDescriptor with { Status = "ready", Ready = true, Message = "GeoLite2 City 只读数据库已就绪。" };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _initialDescriptor = _initialDescriptor with { Status = "notReady", Message = "GeoLite2 City 数据文件不存在或尚未配置。" };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDatabaseException or ArgumentException or NotSupportedException)
        {
            // 不向公开来源信息暴露文件路径或 SDK 的内部异常内容。
            _initialDescriptor = _initialDescriptor with { Status = "unavailable", Message = "GeoLite2 City 文件无法读取或校验失败。" };
        }
        finally
        {
            opened?.Dispose();
        }
    }

    public ProviderDescriptor Descriptor
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                return _initialDescriptor with { Status = "unavailable", Ready = false, Message = "GeoLite2 读取器已停止。" };
            // 启停由管理员决定，不按许可字段或数据年龄自动停用。
            return _initialDescriptor;
        }
    }

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _reader is null)
            return Task.FromResult(new ProviderQueryResult(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version));
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !descriptor.SupportsIpv6)
            return Task.FromResult(new ProviderQueryResult("unsupported", new IpFields(), "该 City 文件不支持 IPv6。", descriptor.Version));

        try
        {
            // 官方 SDK 处理 IPv4 在 IPv6 树中的检索，不手工改成 ::ffff 映射地址。
            var result = _reader.City(address);
            cancellationToken.ThrowIfCancellationRequested();
            var fields = new IpFields
            {
                CountryCode = Present(result.Country.IsoCode),
                Country = Present(result.Country.Name),
                Region = Present(result.MostSpecificSubdivision.Name),
                City = Present(result.City.Name),
                PostalCode = Present(result.Postal.Code),
                Latitude = result.Location.Latitude,
                Longitude = result.Location.Longitude,
                TimeZone = Present(result.Location.TimeZone)
                // RegisteredCountry 不是定位国家，不能兜底；City 库没有 ISP/ASN，不补造。
            };
            return Task.FromResult(new ProviderQueryResult("success", fields, "查询完成；地理位置为数据库推断。", descriptor.Version));
        }
        catch (AddressNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderQueryResult("notFound", new IpFields(), "City 数据库没有此地址的匹配记录。", descriptor.Version));
        }
        catch (Exception exception) when (exception is GeoIP2Exception or InvalidDatabaseException or IOException or ArgumentException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProviderQueryResult("unavailable", new IpFields(), "GeoLite2 City 暂时无法完成查询。", descriptor.Version));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _reader?.Dispose();
    }

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
