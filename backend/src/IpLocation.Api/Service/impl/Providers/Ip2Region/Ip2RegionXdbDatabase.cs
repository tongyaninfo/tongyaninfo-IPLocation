using System.Buffers.Binary;
using System.Net;
using IP2Region.Net.Abstractions;
using IP2Region.Net.XDB;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service.impl.Providers.Ip2Region;

/// <summary>官方 XDB 读取器的有界封装；共享 Seek/Read 通过可取消信号量串行，避免并发位置竞争。</summary>
internal sealed class Ip2RegionXdbDatabase : IDisposable
{
    private const int HeaderBytes = 256;
    private const int VectorBytes = 256 * 256 * 8;
    private readonly ISearcher _searcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public int Family { get; }
    public DateTimeOffset BuiltAtUtc { get; }

    internal Ip2RegionXdbDatabase(int family, DateTimeOffset builtAtUtc, ISearcher searcher)
    {
        Family = family;
        BuiltAtUtc = builtAtUtc;
        _searcher = searcher;
    }

    public static Ip2RegionXdbDatabase Open(string path, int family, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var built = ValidateMetadata(stream, family, maxBytes);
        return new(family, built, new Searcher(CachePolicy.VectorIndex, path));
    }

    internal static DateTimeOffset ValidateMetadata(Stream stream, int family, long maxBytes)
    {
        if (family is not (4 or 6) || maxBytes < HeaderBytes + VectorBytes || stream.Length < HeaderBytes + VectorBytes || stream.Length > maxBytes)
            throw new InvalidDataException("Invalid XDB size or address family.");
        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 3 || BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) != family || BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) != 4)
            throw new InvalidDataException("Only official v3 XDB with four-byte pointers is supported.");
        var first = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var last = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var recordBytes = family == 4 ? 14 : 38;
        if (first < HeaderBytes + VectorBytes || last < first || (last - first) % recordBytes != 0 || (long)last + recordBytes > stream.Length)
            throw new InvalidDataException("Invalid XDB segment index bounds.");
        var vectors = new byte[VectorBytes];
        stream.ReadExactly(vectors);
        for (var offset = 0; offset < vectors.Length; offset += 8)
        {
            var begin = BinaryPrimitives.ReadUInt32LittleEndian(vectors.AsSpan(offset));
            var end = BinaryPrimitives.ReadUInt32LittleEndian(vectors.AsSpan(offset + 4));
            if (begin == 0 && end == 0) continue;
            // 官方 v3 文件最后一个向量使用索引区末尾的排他哨兵，允许它恰好指向最后记录之后。
            var finalSentinel = offset == vectors.Length - 8 && end == (long)last + recordBytes;
            if (begin < first || begin > last || end < begin || (end > last && !finalSentinel) ||
                (begin - first) % recordBytes != 0 || (end - first) % recordBytes != 0)
                throw new InvalidDataException("Invalid XDB vector index pointer.");
        }
        var epoch = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (epoch == 0) throw new InvalidDataException("Missing XDB build epoch.");
        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    public async Task<IpFields?> LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var result = _searcher.Search(address);
            cancellationToken.ThrowIfCancellationRequested();
            return ParseRegion(result);
        }
        finally { _gate.Release(); }
    }

    internal static IpFields? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        // v3 仓库自带数据固定为国家|省份|城市|ISP|国家代码，不能套用旧版“区域”字段位置。
        var values = region.Split('|');
        if (values.Length != 5 || region.Any(char.IsControl)) throw new InvalidDataException("Unsupported XDB region fields.");
        var code = Present(values[4]);
        if (code is not null && (code.Length != 2 || code.Any(character => !char.IsAsciiLetter(character))))
            throw new InvalidDataException("Invalid XDB country code.");
        var fields = new IpFields { Country = Present(values[0]), Region = Present(values[1]), City = Present(values[2]), Isp = Present(values[3]), CountryCode = code?.ToUpperInvariant() };
        return fields.Country is null && fields.Region is null && fields.City is null && fields.Isp is null && fields.CountryCode is null ? null : fields;
    }

    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; _searcher.Dispose(); } }
        finally { _gate.Release(); }
    }

    private static string? Present(string value)
    {
        var text = value.Trim();
        return text is "" or "0" or "-" ? null : text;
    }
}
