using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.Ip2Region;

/// <summary>分别持有只读 IPv4/IPv6 XDB；不下载、复制、重写或自动更新数据文件。</summary>
public sealed class Ip2RegionDataProvider : IIpDataProvider, IDisposable
{
    private readonly ProviderDescriptor _descriptor;
    private readonly Ip2RegionXdbDatabase? _ipv4;
    private readonly Ip2RegionXdbDatabase? _ipv6;
    private int _disposed;
    private int _faulted;

    public Ip2RegionDataProvider(IOptions<Ip2RegionOptions> options) : this(options, Ip2RegionXdbDatabase.Open) { }

    internal Ip2RegionDataProvider(IOptions<Ip2RegionOptions> options, Func<string, int, long, Ip2RegionXdbDatabase> openDatabase)
    {
        var configuration = options.Value;
        _descriptor = new("ip2region", "ip2region", "notReady", false, false, configuration.Enabled, configuration.LicenseConfirmed,
            false, null, "尚未提供并验证 ip2region v3 XDB 文件。", "https://github.com/lionsoul2014/ip2region/blob/master/LICENSE.md",
            "ip2region 开源仓库自带数据 (https://github.com/lionsoul2014/ip2region)");
        if (!configuration.Enabled)
        {
            _descriptor = _descriptor with { Status = "disabled", Message = "ip2region 来源已停用。" };
            return;
        }
        // 授权由人工处理；兼容字段不参与技术读取资格判断。
        if (configuration.MaxDatabaseBytes is < 524_558 or > 4_294_967_295)
        {
            _descriptor = _descriptor with { Status = "unavailable", Message = "ip2region 文件资源上限配置无效。" };
            return;
        }
        if (string.IsNullOrWhiteSpace(configuration.Ipv4DatabasePath) && string.IsNullOrWhiteSpace(configuration.Ipv6DatabasePath)) return;
        Ip2RegionXdbDatabase? ipv4 = null;
        Ip2RegionXdbDatabase? ipv6 = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(configuration.Ipv4DatabasePath)) ipv4 = openDatabase(configuration.Ipv4DatabasePath, 4, configuration.MaxDatabaseBytes);
            if (!string.IsNullOrWhiteSpace(configuration.Ipv6DatabasePath)) ipv6 = openDatabase(configuration.Ipv6DatabasePath, 6, configuration.MaxDatabaseBytes);
            _ipv4 = ipv4;
            _ipv6 = ipv6;
            ipv4 = ipv6 = null;
            _descriptor = _descriptor with
            {
                Status = "ready", Ready = true, SupportsIpv4 = _ipv4 is not null, SupportsIpv6 = _ipv6 is not null,
                Version = string.Join(";", new[] { _ipv4 is null ? null : "v4=" + Version(_ipv4), _ipv6 is null ? null : "v6=" + Version(_ipv6) }.OfType<string>()),
                Message = "ip2region v3 文件头、地址族与向量索引边界已验证；IPv4/IPv6 分别只读查询。"
            };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _descriptor = _descriptor with { Message = "配置的 ip2region 文件不存在，未启用部分文件冒充完整就绪。" };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            _descriptor = _descriptor with { Status = "unavailable", Message = "ip2region 文件无法读取或未通过结构校验。" };
        }
        finally { ipv4?.Dispose(); ipv6?.Dispose(); }
    }

    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
        ? _descriptor with { Status = "unavailable", Ready = false, Message = "ip2region 只读数据库当前不可用。" } : _descriptor;

    public async Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready) return new(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var database = address.AddressFamily == AddressFamily.InterNetwork ? _ipv4 : _ipv6;
        if (database is null) return new("unsupported", new IpFields(), "当前 ip2region 未配置此地址族文件。", descriptor.Version);
        try
        {
            var fields = await database.LookupAsync(address, cancellationToken);
            return fields is null ? new("notFound", new IpFields(), "ip2region 没有此地址的可用记录。", Version(database))
                : new("success", fields, "查询完成；地理信息和运营商为该版本数据记录。", Version(database));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _faulted, 1);
            return new("unavailable", new IpFields(), "ip2region 暂时无法完成查询。", Version(database));
        }
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _ipv4?.Dispose(); _ipv6?.Dispose(); } }
    private static string Version(Ip2RegionXdbDatabase database) => database.BuiltAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
