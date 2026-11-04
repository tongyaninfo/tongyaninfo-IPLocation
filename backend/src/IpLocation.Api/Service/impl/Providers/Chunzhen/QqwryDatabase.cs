using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service.impl.Providers.Chunzhen;

/// <summary>QQWry 小端索引与有界重定向读取；只读映射可并发查询，不共享 Seek 位置。</summary>
internal sealed class QqwryDatabase : IDisposable
{
    private const int MaxStringBytes = 4096;
    private const int MaxRedirectDepth = 16;
    private static readonly Encoding Gbk = CreateEncoding();
    private readonly long _length;
    private readonly Func<long, byte> _byte;
    private readonly Func<long, uint> _uint;
    private readonly IDisposable? _owner;
    private readonly uint _indexStart;
    private readonly int _count;
    private int _disposed;
    public string Version { get; }

    private QqwryDatabase(long length, Func<long, byte> readByte, Func<long, uint> readUInt32,
        IDisposable? owner, long maxBytes, int maxEntries, Func<string> contentHash)
    {
        _length = length; _byte = readByte; _uint = readUInt32; _owner = owner;
        if (length < 15 || length > maxBytes || maxEntries is < 1 or > 10_000_000)
            throw new InvalidDataException("Invalid QQWry resource bounds.");
        _indexStart = ReadUInt32(0);
        var lastIndex = ReadUInt32(4);
        if (_indexStart < 8 || lastIndex < _indexStart || (lastIndex - _indexStart) % 7 != 0 || (long)lastIndex + 7 > length)
            throw new InvalidDataException("Invalid QQWry index bounds.");
        var count = ((long)lastIndex - _indexStart) / 7 + 1;
        if (count > maxEntries) throw new InvalidDataException("Too many QQWry index entries.");
        _count = checked((int)count);
        uint previousStart = 0;
        for (var index = 0; index < _count; index++)
        {
            var location = (long)_indexStart + index * 7L;
            var start = ReadUInt32(location);
            var record = ReadUInt24(location + 4);
            EnsureData(record, 5);
            var end = ReadUInt32(record);
            // QQWry 的保留地址记录可能有覆盖重叠；保证起始索引严格递增，每次按最大起始地址选中再核验本条结束地址。
            if (end < start || index > 0 && start <= previousStart)
                throw new InvalidDataException("QQWry IP index order or range is invalid.");
            previousStart = start;
        }
        var lastRecord = ReadUInt24((long)lastIndex + 4);
        var metadata = ReadLocation(lastRecord + 4, new HashSet<long>(), 0);
        var date = Regex.Match(metadata.Area ?? "", @"(?<year>\d{4})年(?<month>\d{1,2})月(?<day>\d{1,2})日", RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (date.Success && DateOnly.TryParseExact($"{date.Groups["year"].Value}-{date.Groups["month"].Value}-{date.Groups["day"].Value}",
                "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var published))
            Version = published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else Version = "sha256:" + contentHash();
    }

    public static QqwryDatabase Open(string path, long maxBytes, int maxEntries)
    {
        var storage = new MappedStorage(path);
        try
        {
            return new(storage.Length, storage.ReadByte, storage.ReadUInt32, storage, maxBytes, maxEntries, () =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Convert.ToHexStringLower(SHA256.HashData(stream));
            });
        }
        catch { storage.Dispose(); throw; }
    }

    internal static QqwryDatabase FromBytes(byte[] data, int maxEntries = 10_000_000) => new(data.Length,
        offset => data[checked((int)offset)], offset => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)offset), 4)),
        null, 268_435_456, maxEntries, () => Convert.ToHexStringLower(SHA256.HashData(data)));

    public IpFields? Lookup(IPAddress address)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
        Span<byte> bytes = stackalloc byte[4]; address.TryWriteBytes(bytes, out _);
        var ip = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var low = 0; var high = _count - 1; var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var start = ReadUInt32((long)_indexStart + middle * 7L);
            if (start <= ip) { found = middle; low = middle + 1; } else high = middle - 1;
        }
        if (found < 0) return null;
        var record = ReadUInt24((long)_indexStart + found * 7L + 4);
        if (ip > ReadUInt32(record)) return null;
        var result = ReadLocation(record + 4, new HashSet<long>(), 0);
        if (result.Region is null && result.Area is null) return null;
        // DAT 提供的是地区文本与网络描述，不凭分隔符推断国家、省市或精确位置。
        return new IpFields { Region = result.Region, Isp = result.Area };
    }

    private (string? Region, string? Area) ReadLocation(long position, HashSet<long> stack, int depth)
    {
        Enter(position, stack, depth);
        try
        {
            var mode = ReadDataByte(position);
            if (mode == 1) return ReadLocation(ReadDataPointer(position + 1), stack, depth + 1);
            if (mode == 2) return (ReadText(ReadDataPointer(position + 1), stack, depth + 1), ReadText(position + 4, stack, depth + 1));
            var text = ReadCString(position);
            return (text.Text, ReadText(text.Next, stack, depth + 1));
        }
        finally { stack.Remove(position); }
    }

    private string? ReadText(long position, HashSet<long> stack, int depth)
    {
        if (position == 0) return null;
        Enter(position, stack, depth);
        try
        {
            var mode = ReadDataByte(position);
            return mode is 1 or 2 ? ReadText(ReadDataPointer(position + 1), stack, depth + 1) : ReadCString(position).Text;
        }
        finally { stack.Remove(position); }
    }

    private (string? Text, long Next) ReadCString(long position)
    {
        var bytes = new byte[Math.Min(MaxStringBytes, checked((int)(_indexStart - position)))];
        for (var index = 0; index < bytes.Length; index++)
        {
            var current = ReadDataByte(position + index);
            if (current == 0)
            {
                var text = Gbk.GetString(bytes, 0, index).Trim();
                if (text.Any(char.IsControl)) throw new InvalidDataException("Unsafe QQWry text.");
                return (string.IsNullOrWhiteSpace(text) || text.Equals("CZ88.NET", StringComparison.OrdinalIgnoreCase) ? null : text, position + index + 1);
            }
            bytes[index] = current;
        }
        throw new InvalidDataException("QQWry text is unterminated or too long.");
    }

    private void Enter(long position, HashSet<long> stack, int depth)
    {
        EnsureData(position, 1);
        if (depth > MaxRedirectDepth || !stack.Add(position)) throw new InvalidDataException("QQWry redirect cycle or depth exceeded.");
    }
    private byte ReadDataByte(long offset) { EnsureData(offset, 1); return _byte(offset); }
    private uint ReadDataPointer(long offset) { EnsureData(offset, 3); return ReadUInt24(offset); }
    private uint ReadUInt24(long offset)
    {
        EnsureTotal(offset, 3);
        return (uint)(_byte(offset) | _byte(offset + 1) << 8 | _byte(offset + 2) << 16);
    }
    private uint ReadUInt32(long offset) { EnsureTotal(offset, 4); return _uint(offset); }
    private void EnsureTotal(long position, int count)
    { if (position < 0 || position > _length - count) throw new InvalidDataException("QQWry read is outside file."); }
    private void EnsureData(long position, int count)
    { if (position < 8 || position > (long)_indexStart - count) throw new InvalidDataException("QQWry data pointer is outside data section."); }
    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner?.Dispose(); }

    private sealed class MappedStorage : IDisposable
    {
        private readonly FileStream _stream;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        public long Length => _stream.Length;
        public MappedStorage(string path)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                _map = MemoryMappedFile.CreateFromFile(_stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
                try { _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
                catch { _map.Dispose(); throw; }
            }
            catch { _stream.Dispose(); throw; }
        }
        public byte ReadByte(long offset) => _view.ReadByte(offset);
        public uint ReadUInt32(long offset)
        { var value = _view.ReadUInt32(offset); return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value); }
        public void Dispose() { _view.Dispose(); _map.Dispose(); _stream.Dispose(); }
    }
}
