using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using MaxMind.Db;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl.Providers.Ipinfo;

/// <summary>复用已有 MaxMind.Db 低层只读读取器查询 IPinfo Lite，不联网或访问下载凭据。</summary>
public sealed class IpinfoLiteDataProvider : IIpDataProvider, IDisposable
{
    private const string DatabaseType = "ipinfo bundle_location_lite.mmdb";
    private readonly ProviderDescriptor _descriptor;
    private readonly Reader? _reader;
    private int _disposed;
    private int _faulted;

    public IpinfoLiteDataProvider(IOptions<IpinfoOptions> options)
        : this(options, path => new Reader(path, FileAccessMode.MemoryMapped)) { }

    internal IpinfoLiteDataProvider(IOptions<IpinfoOptions> options, Func<string, Reader> openReader)
    {
        var configuration = options.Value;
        _descriptor = new("ipinfo", "IPinfo Lite", "notReady", true, true, configuration.Enabled,
            configuration.LicenseConfirmed, false, null, "尚未提供并验证 IPinfo Lite MMDB 文件。",
            "https://creativecommons.org/licenses/by-sa/4.0/", "IP address data is powered by IPinfo (https://ipinfo.io)");
        if (!configuration.Enabled)
        {
            _descriptor = _descriptor with { Status = "disabled", Message = "IPinfo Lite 来源已停用。" };
            return;
        }
        // 授权由人工处理；兼容字段不参与技术读取资格判断。
        if (string.IsNullOrWhiteSpace(configuration.DatabasePath)) return;
        Reader? opened = null;
        try
        {
            opened = openReader(configuration.DatabasePath);
            if (opened.Metadata.DatabaseType != DatabaseType || opened.Metadata.IPVersion is not (4 or 6))
                throw new InvalidDataException("Unsupported IPinfo Lite metadata.");
            // 版本只使用 MMDB 自述构建时间，不从下载时间或文件名编造版本。
            var built = new DateTimeOffset(DateTime.SpecifyKind(opened.Metadata.BuildDate, DateTimeKind.Utc));
            _descriptor = _descriptor with
            {
                Status = "ready", Ready = true, SupportsIpv6 = opened.Metadata.IPVersion == 6,
                Version = built.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Message = "IPinfo Lite 国家、大洲和 ASN 只读数据库已就绪，不含省市或精确位置。"
            };
            _reader = opened;
            opened = null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _descriptor = _descriptor with { Message = "IPinfo Lite 文件不存在或尚未配置。" };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidDatabaseException or DeserializationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _descriptor = _descriptor with { Status = "unavailable", Message = "IPinfo Lite 文件无法读取或未通过类型校验。" };
        }
        finally { opened?.Dispose(); }
    }

    public ProviderDescriptor Descriptor => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0
        ? _descriptor with { Status = "unavailable", Ready = false, Message = "IPinfo Lite 只读数据库当前不可用。" } : _descriptor;

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Descriptor;
        if (!descriptor.Ready || _reader is null)
            return Task.FromResult(new ProviderQueryResult(descriptor.Status, new IpFields(), descriptor.Message, descriptor.Version));
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !descriptor.SupportsIpv6)
            return Task.FromResult(new ProviderQueryResult("unsupported", new IpFields(), "该 IPinfo Lite 文件不支持 IPv6。", descriptor.Version));
        try
        {
            var record = _reader.Find<IpinfoLiteRecord>(address);
            cancellationToken.ThrowIfCancellationRequested();
            var fields = record is null ? null : Fields(record);
            return Task.FromResult(fields is null
                ? new ProviderQueryResult("notFound", new IpFields(), "IPinfo Lite 没有此地址的可用记录。", descriptor.Version)
                : new ProviderQueryResult("success", fields, "查询完成；Lite 仅提供国家、大洲和 ASN 信息。", descriptor.Version));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidDatabaseException or DeserializationException or ObjectDisposedException or ArgumentException or XmlException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _faulted, 1);
            // MMDB 异常可能包含原始字段或本机路径，统一固定消息以免泄露。
            return Task.FromResult(new ProviderQueryResult("unavailable", new IpFields(), "IPinfo Lite 暂时无法完成查询。", descriptor.Version));
        }
    }

    private static IpFields? Fields(IpinfoLiteRecord record)
    {
        var fields = new IpFields
        {
            CountryCode = Present(record.CountryCode), Country = Present(record.Country),
            ContinentCode = Present(record.ContinentCode), Continent = Present(record.Continent),
            Asn = Present(record.Asn), AsName = Present(record.AsName), AsDomain = Present(record.AsDomain)
            // AS 名称不是 ISP；ASN 保留 AS 前缀，域名仅作数据文本，不作为请求地址。
        };
        return fields.CountryCode is null && fields.Country is null && fields.ContinentCode is null && fields.Continent is null &&
               fields.Asn is null && fields.AsName is null && fields.AsDomain is null ? null : fields;
    }

    private static string? Present(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Any(char.IsControl)) throw new InvalidDataException("Unsafe IPinfo text field.");
        return XmlConvert.VerifyXmlChars(value);
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _reader?.Dispose(); }
}
