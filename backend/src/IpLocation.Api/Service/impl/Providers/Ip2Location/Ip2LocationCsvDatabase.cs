using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using IpLocation.Api.Models.Responses;
using Microsoft.Win32.SafeHandles;

namespace IpLocation.Api.Service.impl.Providers.Ip2Location;

/// <summary>
/// DB11 IPv4 CSV 只读索引。每条仅保存起止 uint 和记录字节偏移（16字节）；
/// 长度从下一偏移或文件末尾推导。原始字符串只在当前行校验/查询时短暂存在。
/// </summary>
public sealed class Ip2LocationCsvDatabase : IDisposable
{
    private const int ScanBufferBytes = 65_536;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlySource _source;
    private readonly RangeEntry[] _entries;
    private readonly int _maxRecordBytes;
    private int _disposed;

    private Ip2LocationCsvDatabase(IReadOnlySource source, RangeEntry[] entries, string sha256, int maxRecordBytes)
    {
        _source = source;
        _entries = entries;
        _maxRecordBytes = maxRecordBytes;
        Sha256 = sha256;
        FileLength = source.Length;
    }

    public int EntryCount => _entries.Length;
    public long FileLength { get; }
    public string Sha256 { get; }
    public uint MinStart => _entries[0].Start;
    public uint MaxEnd => _entries[^1].End;

    public static Ip2LocationCsvDatabase Open(string path, CancellationToken cancellationToken = default,
        int maxRecordBytes = 1_048_576, int maxEntries = 10_000_000)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Build(new FileSource(path), cancellationToken, maxRecordBytes, maxEntries);
    }

    /// <summary>受控测试可使用内存流；流必须可寻址，默认由本读取器负责释放。</summary>
    public static Ip2LocationCsvDatabase Load(Stream stream, CancellationToken cancellationToken = default,
        int maxRecordBytes = 1_048_576, int maxEntries = 10_000_000, bool leaveOpen = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        return Build(new StreamSource(stream, leaveOpen), cancellationToken, maxRecordBytes, maxEntries);
    }

    public IpFields? Lookup(uint ipv4, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var low = 0;
        var high = _entries.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var entry = _entries[middle];
            if (ipv4 < entry.Start) high = middle - 1;
            else if (ipv4 > entry.End) low = middle + 1;
            else return ReadFields(middle, cancellationToken);
        }

        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _source.Dispose();
    }

    private static Ip2LocationCsvDatabase Build(IReadOnlySource source, CancellationToken cancellationToken,
        int maxRecordBytes, int maxEntries)
    {
        try
        {
            if (maxRecordBytes is < 1 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
            if (maxEntries is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            if (source.Length == 0) throw InvalidCsv();
            var entries = new List<RangeEntry>(Math.Min(65_536, maxEntries));
            var record = new ArrayBufferWriter<byte>(Math.Min(512, maxRecordBytes));
            var buffer = ArrayPool<byte>.Shared.Rent(ScanBufferBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                var state = CsvState.FieldStart;
                long recordOffset = 0;
                long position = 0;
                while (position < source.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(ScanBufferBytes, source.Length - position);
                    ReadExactly(source, buffer.AsSpan(0, count), position, cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    var start = 0;
                    if (position == 0 && count >= 3 && buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf)
                    {
                        // BOM 属于文件内容指纹，但不属于第一条 CSV 记录。
                        start = 3;
                        recordOffset = 3;
                    }

                    for (var index = start; index < count; index++)
                    {
                        if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var value = buffer[index];
                        if (record.WrittenCount >= maxRecordBytes) throw InvalidCsv();
                        record.GetSpan(1)[0] = value;
                        record.Advance(1);
                        if (Advance(ref state, value))
                        {
                            AddRecord(record.WrittenSpan, recordOffset);
                            record.Clear();
                            recordOffset = position + index + 1;
                        }
                    }
                    position += count;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (state is CsvState.Quoted or CsvState.RecordCr) throw InvalidCsv();
                if (record.WrittenCount > 0) AddRecord(record.WrittenSpan, recordOffset);
                if (entries.Count == 0) throw InvalidCsv();
                return new Ip2LocationCsvDatabase(source, entries.ToArray(), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), maxRecordBytes);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            void AddRecord(ReadOnlySpan<byte> bytes, long offset)
            {
                if (entries.Count >= maxEntries) throw InvalidCsv();
                Span<CsvField> fields = stackalloc CsvField[10];
                ParseFields(bytes, fields);
                ValidateEncoding(bytes, fields);
                var from = ReadNumber(bytes, fields[0]);
                var to = ReadNumber(bytes, fields[1]);
                if (from > to || (entries.Count > 0 && from <= entries[^1].End)) throw InvalidCsv();
                _ = ReadCoordinate(bytes, fields[6], 90);
                _ = ReadCoordinate(bytes, fields[7], 180);
                entries.Add(new RangeEntry(from, to, offset));
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private IpFields? ReadFields(int index, CancellationToken cancellationToken)
    {
        var entry = _entries[index];
        var end = index + 1 < _entries.Length ? _entries[index + 1].Offset : FileLength;
        var length = checked((int)(end - entry.Offset));
        if (length is <= 0 || length > _maxRecordBytes) throw InvalidCsv();
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var bytes = rented.AsSpan(0, length);
            ReadExactly(_source, bytes, entry.Offset, cancellationToken);
            Span<CsvField> fields = stackalloc CsvField[10];
            ParseFields(bytes, fields);
            ValidateEncoding(bytes, fields);
            if (ReadNumber(bytes, fields[0]) != entry.Start || ReadNumber(bytes, fields[1]) != entry.End) throw InvalidCsv();
            var countryCode = Decode(bytes, fields[2]);
            var country = Decode(bytes, fields[3]);
            var region = Decode(bytes, fields[4]);
            var city = Decode(bytes, fields[5]);
            var postalCode = Decode(bytes, fields[8]);
            var timeZoneOffset = Decode(bytes, fields[9]);
            var latitude = ReadCoordinate(bytes, fields[6], 90);
            var longitude = ReadCoordinate(bytes, fields[7], 180);
            var hasLocation = countryCode is not null || country is not null || region is not null || city is not null || postalCode is not null;
            // 只有来源完全缺少定位信息时，才把成对的零/缺省坐标看成占位值。
            // 赤道、本初子午线及真实(0,0)在有定位信息时均保留。
            if (!hasLocation && (latitude is null or 0) && (longitude is null or 0))
            {
                latitude = null;
                longitude = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasLocation && latitude is null && longitude is null && timeZoneOffset is null) return null;
            return new IpFields
            {
                CountryCode = countryCode,
                Country = country,
                Region = region,
                City = city,
                Latitude = latitude,
                Longitude = longitude,
                PostalCode = postalCode,
                TimeZone = timeZoneOffset
                // DB11 不含 ISP/ASN；TimeZone 保留来源 UTC 偏移，不转换为 IANA 名称。
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void ParseFields(ReadOnlySpan<byte> record, Span<CsvField> fields)
    {
        var length = record.Length;
        if (length > 0 && record[length - 1] == (byte)'\n')
        {
            length--;
            if (length > 0 && record[length - 1] == (byte)'\r') length--;
        }
        var position = 0;
        var count = 0;
        while (true)
        {
            if (count == fields.Length) throw InvalidCsv();
            if (position < length && record[position] == (byte)'"')
            {
                var start = ++position;
                var closed = false;
                while (position < length)
                {
                    if (record[position] != (byte)'"') { position++; continue; }
                    if (position + 1 < length && record[position + 1] == (byte)'"') { position += 2; continue; }
                    fields[count++] = new CsvField(start, position - start, true);
                    position++;
                    closed = true;
                    break;
                }
                if (!closed || (position < length && record[position] != (byte)',')) throw InvalidCsv();
            }
            else
            {
                var start = position;
                while (position < length && record[position] != (byte)',')
                {
                    if (record[position] is (byte)'"' or (byte)'\r' or (byte)'\n') throw InvalidCsv();
                    position++;
                }
                fields[count++] = new CsvField(start, position - start, false);
            }
            if (position == length) break;
            position++;
        }
        if (count != 10) throw InvalidCsv();
    }

    private static void ValidateEncoding(ReadOnlySpan<byte> record, ReadOnlySpan<CsvField> fields)
    {
        try
        {
            foreach (var field in fields)
            {
                var value = record.Slice(field.Offset, field.Length);
                // 禁止不能安全表示为XML/文本字段的控制字符；引号内换行和制表符仍按CSV内容保留。
                foreach (var item in value)
                    if ((item < 0x20 && item is not (byte)'\r' and not (byte)'\n' and not (byte)'\t') || item == 0x7f)
                        throw InvalidCsv();
                _ = StrictUtf8.GetCharCount(value);
            }
        }
        catch (DecoderFallbackException)
        {
            throw InvalidCsv();
        }
    }

    private static uint ReadNumber(ReadOnlySpan<byte> record, CsvField field)
    {
        var value = TrimAscii(record.Slice(field.Offset, field.Length));
        foreach (var item in value) if (item is < (byte)'0' or > (byte)'9') throw InvalidCsv();
        if (value.Length == 0 || !Utf8Parser.TryParse(value, out uint number, out var consumed) || consumed != value.Length) throw InvalidCsv();
        return number;
    }

    private static double? ReadCoordinate(ReadOnlySpan<byte> record, CsvField field, int bound)
    {
        var value = TrimAscii(record.Slice(field.Offset, field.Length));
        if (value.Length == 0 || value.SequenceEqual("-"u8)) return null;
        if (!Utf8Parser.TryParse(value, out double number, out var consumed) || consumed != value.Length ||
            !double.IsFinite(number) || number < -bound || number > bound) throw InvalidCsv();
        return number;
    }

    private static string? Decode(ReadOnlySpan<byte> record, CsvField field)
    {
        var text = StrictUtf8.GetString(record.Slice(field.Offset, field.Length));
        if (field.Quoted) text = text.Replace("\"\"", "\"", StringComparison.Ordinal);
        text = text.Trim();
        return text.Length == 0 || text == "-" ? null : text;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] is (byte)' ' or (byte)'\t') start++;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t') end--;
        return value[start..end];
    }

    private static void ReadExactly(IReadOnlySource source, Span<byte> destination, long offset, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source.Read(destination[read..], offset + read);
            if (count == 0) throw InvalidCsv();
            read += count;
        }
    }

    private static bool Advance(ref CsvState state, byte value)
    {
        if (state == CsvState.Quoted)
        {
            if (value == (byte)'"') state = CsvState.AfterQuote;
            return false;
        }
        if (state == CsvState.RecordCr)
        {
            if (value != (byte)'\n') throw InvalidCsv();
            state = CsvState.FieldStart;
            return true;
        }
        if (state == CsvState.AfterQuote && value == (byte)'"')
        {
            state = CsvState.Quoted;
            return false;
        }
        if (value == (byte)'\n') { state = CsvState.FieldStart; return true; }
        if (value == (byte)'\r') { state = CsvState.RecordCr; return false; }
        if (value == (byte)',') { state = CsvState.FieldStart; return false; }
        if (value == (byte)'"')
        {
            if (state != CsvState.FieldStart) throw InvalidCsv();
            state = CsvState.Quoted;
            return false;
        }
        if (state == CsvState.AfterQuote) throw InvalidCsv();
        state = CsvState.Unquoted;
        return false;
    }

    private static InvalidDataException InvalidCsv() => new("IP2Location DB11 IPv4 CSV 格式、地址范围或资源限制校验失败。");
    private readonly record struct RangeEntry(uint Start, uint End, long Offset);
    private readonly record struct CsvField(int Offset, int Length, bool Quoted);
    private enum CsvState { FieldStart, Unquoted, Quoted, AfterQuote, RecordCr }

    private interface IReadOnlySource : IDisposable
    {
        long Length { get; }
        int Read(Span<byte> destination, long offset);
    }

    private sealed class FileSource : IReadOnlySource
    {
        private readonly SafeFileHandle _handle;

        public FileSource(string path)
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            try { Length = RandomAccess.GetLength(_handle); }
            catch { _handle.Dispose(); throw; }
        }

        public long Length { get; }
        public int Read(Span<byte> destination, long offset) => RandomAccess.Read(_handle, destination, offset);
        public void Dispose() => _handle.Dispose();
    }

    private sealed class StreamSource : IReadOnlySource
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly object _gate = new();

        public StreamSource(Stream stream, bool leaveOpen)
        {
            if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("CSV流必须支持只读寻址。", nameof(stream));
            _stream = stream;
            _leaveOpen = leaveOpen;
            Length = stream.Length;
        }

        public long Length { get; }
        public int Read(Span<byte> destination, long offset)
        {
            // 仅测试/受控流接缝需要同步流位置；生产句柄使用不共享Position的RandomAccess。
            lock (_gate)
            {
                _stream.Position = offset;
                return _stream.Read(destination);
            }
        }
        public void Dispose() { if (!_leaveOpen) _stream.Dispose(); }
    }
}
