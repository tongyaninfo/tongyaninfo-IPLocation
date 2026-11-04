using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.Chunzhen;

/// <summary>纯真 QQWry IPv4 DAT 只读适配；启用、文件就绪分别表达，不联网或推导行政区。</summary>
public sealed class ChunzhenDataProvider : IIpDataProvider, IDisposable
{
    private readonly ProviderDescriptor _descriptor;
    private readonly QqwryDatabase? _database;
    private int _disposed;
    private int _faulted;
    public ChunzhenDataProvider(IOptions<ChunzhenOptions> options) : this(options, QqwryDatabase.Open) { }
    internal ChunzhenDataProvider(IOptions<ChunzhenOptions> options, Func<string, long, int, QqwryDatabase> open)
    {
        var value = options.Value;
        _descriptor = new(ProviderCodes.Cz88, "纯真 社区版", "notReady", true, false, value.Enabled, false, false, null,
            "尚未配置纯真数据文件/读取器。", "https://www.cz88.net/geo-public", "IP地址位置数据由纯真CZ88提供支持 (https://www.cz88.net)");
        if (!value.Enabled) { _descriptor = _descriptor with { Status = "disabled", Message = "纯真来源已停用。" }; return; }
        if (string.IsNullOrWhiteSpace(value.DatabasePath)) return;
        try
        {
            _database = open(value.DatabasePath, value.MaxDatabaseBytes, value.MaxIndexEntries);
            _descriptor = _descriptor with { Status = "ready", Ready = true, Version = _database.Version,
                Message = "纯真 QQWry IPv4 只读文件已就绪；地区与网络描述保持原文。" };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        { _descriptor = _descriptor with { Status = "unavailable", Message = "纯真 DAT 文件无法读取或未通过结构校验。" }; }
    }
    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
        ? _descriptor with { Ready = false, Status = "unavailable", Message = "纯真读取器当前不可用。" } : _descriptor;
    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _database is null) return Task.FromResult(new ProviderQueryResult(descriptor.Status, new(), descriptor.Message, descriptor.Version));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return Task.FromResult(new ProviderQueryResult("unsupported", new(), "纯真 QQWry DAT 仅支持 IPv4。", descriptor.Version));
        try
        {
            var fields = _database.Lookup(address);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(fields is null ? new ProviderQueryResult("notFound", new(), "此纯真版本没有匹配记录。", descriptor.Version)
                : new ProviderQueryResult("success", fields, "查询完成；地区原文与网络描述来自纯真 DAT。", descriptor.Version));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or ObjectDisposedException or OverflowException)
        {
            Interlocked.Exchange(ref _faulted, 1);
            return Task.FromResult(new ProviderQueryResult("unavailable", new(), "纯真 DAT 记录读取失败。", descriptor.Version));
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _database?.Dispose(); }
}
