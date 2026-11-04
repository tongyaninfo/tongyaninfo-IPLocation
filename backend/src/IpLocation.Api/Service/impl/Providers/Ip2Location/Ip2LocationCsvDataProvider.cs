using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.Ip2Location;

/// <summary>启动时校验并建立内存索引，只读查询实际 CSV；不复制、修改或生成磁盘索引。</summary>
public sealed class Ip2LocationCsvDataProvider : IIpDataProvider, IDisposable
{
    private const string LicenseUrl = "https://www.ip2location.com/lite-terms";
    private const string Attribution = "通研信息 uses the IP2Location LITE database for IP geolocation (https://lite.ip2location.com).";
    private readonly ProviderDescriptor _initialDescriptor;
    private readonly Ip2LocationCsvDatabase? _database;
    private int _disposed;
    private int _faulted;

    public Ip2LocationCsvDataProvider(IOptions<Ip2LocationOptions> options)
        : this(options, (path, token) => Ip2LocationCsvDatabase.Open(path, token, options.Value.MaxRecordBytes, options.Value.MaxIndexEntries))
    {
    }

    internal Ip2LocationCsvDataProvider(IOptions<Ip2LocationOptions> options,
        Func<string, CancellationToken, Ip2LocationCsvDatabase> openDatabase)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openDatabase);
        var configuration = options.Value;
        _initialDescriptor = new("ip2location", "IP2Location LITE DB11", "notReady", true, false,
            configuration.Enabled, configuration.LicenseConfirmed, false, null,
            "尚未提供并验证 IP2Location DB11 IPv4 CSV 文件。", LicenseUrl, Attribution);

        if (!configuration.Enabled)
        {
            _initialDescriptor = _initialDescriptor with { Status = "disabled", Message = "IP2Location LITE 来源已停用。" };
            return;
        }
        // 历史CSV读取链也不根据授权字段拦截，避免回切格式后重新引入业务门槛。
        if (configuration.MaxRecordBytes is < 1 or > 16_777_216 || configuration.MaxIndexEntries is < 1 or > 100_000_000 ||
            configuration.DataVersion is { Length: > 128 } || configuration.DataVersion?.Any(char.IsControl) == true)
        {
            _initialDescriptor = _initialDescriptor with { Status = "unavailable", Message = "IP2Location 读取资源或版本配置无效。" };
            return;
        }
        if (string.IsNullOrWhiteSpace(configuration.DatabasePath)) return;

        try
        {
            _database = openDatabase(configuration.DatabasePath, CancellationToken.None);
            var version = string.IsNullOrWhiteSpace(configuration.DataVersion) ? null : configuration.DataVersion.Trim();
            if (version is null && configuration.UseContentHashAsVersion) version = "sha256:" + _database.Sha256;
            _initialDescriptor = _initialDescriptor with
            {
                Status = "ready", Ready = true, Version = version,
                Message = "DB11 IPv4 CSV 全量校验和只读索引已就绪；timeZone 为来源 UTC 偏移，非 IANA 时区。" +
                          (configuration.UseContentHashAsVersion && string.IsNullOrWhiteSpace(configuration.DataVersion)
                              ? " 版本为 SHA-256 内容指纹，不代表官方发布日期。" : "")
            };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _initialDescriptor = _initialDescriptor with { Status = "notReady", Message = "IP2Location 数据文件不存在或尚未配置。" };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            // 异常正文可能含私有路径；公开信息只返回固定错误，不暴露文件内容。
            _initialDescriptor = _initialDescriptor with { Status = "unavailable", Message = "IP2Location DB11 IPv4 CSV 无法读取或未通过全量校验。" };
        }
    }

    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
        ? _initialDescriptor with { Status = "unavailable", Ready = false, Message = "IP2Location 只读数据库当前不可用。" }
        : _initialDescriptor;

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _database is null)
            return Task.FromResult(new ProviderQueryResult(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return Task.FromResult(new ProviderQueryResult("unsupported", new IpFields(), "当前 DB11 CSV 经全量验证仅覆盖 IPv4，不支持 IPv6。", descriptor.Version));

        try
        {
            var number = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
            var fields = _database.Lookup(number, cancellationToken);
            return Task.FromResult(fields is null
                ? new ProviderQueryResult("notFound", new IpFields(), "DB11 没有该地址的可用定位记录。", descriptor.Version)
                : new ProviderQueryResult("success", fields, "查询完成；地理位置为数据库推断，timeZone 保留来源 UTC 偏移而非 IANA 时区。", descriptor.Version));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ObjectDisposedException or ArgumentException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _faulted, 1);
            return Task.FromResult(new ProviderQueryResult("unavailable", new IpFields(), "IP2Location 只读数据暂时无法完成查询。", descriptor.Version));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _database?.Dispose();
    }
}
