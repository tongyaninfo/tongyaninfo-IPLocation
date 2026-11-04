using System.Buffers.Binary;
using System.Text;

namespace IpLocation.Api.Tests.Common;

/// <summary>可复用的纯内存 MMDB 夹具，不含真实来源记录或磁盘文件。</summary>
internal static class ControlledMmdb
{
    public static Dictionary<string, object> Map(params (string Key, object Value)[] entries) => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    public static MemoryStream Create(string type, int family = 6, ulong buildEpoch = 1793501546UL, Dictionary<string, object>? record = null)
    {
        var bytes = new List<byte>();
        var pointer = (byte)(record is null ? 1 : 17);
        bytes.AddRange([0, 0, pointer, 0, 0, pointer]);
        bytes.AddRange(new byte[16]);
        if (record is not null) Write(record);
        bytes.AddRange([0xab, 0xcd, 0xef]);
        bytes.AddRange(Encoding.ASCII.GetBytes("MaxMind.com"));
        Write(Map(("binary_format_major_version", 2), ("binary_format_minor_version", 0),
            ("build_epoch", buildEpoch), ("database_type", type),
            ("description", Map(("en", "Controlled MMDB format fixture"))),
            ("ip_version", family), ("node_count", 1U), ("record_size", 24), ("languages", new object[] { "zh-CN", "en" })));
        return new MemoryStream(bytes.ToArray(), writable: false);

        void Header(int kind, int size)
        {
            var encodedSize = size < 29 ? size : 29;
            bytes.Add((byte)(((kind < 8 ? kind : 0) << 5) | encodedSize));
            if (kind >= 8) bytes.Add((byte)(kind - 7));
            if (size >= 29) bytes.Add((byte)(size - 29));
        }
        void Integer(int kind, ulong value)
        {
            var data = new List<byte>();
            while (value > 0) { data.Insert(0, (byte)value); value >>= 8; }
            Header(kind, data.Count); bytes.AddRange(data);
        }
        void Write(object value)
        {
            switch (value)
            {
                case string text:
                    var utf8 = Encoding.UTF8.GetBytes(text); Header(2, utf8.Length); bytes.AddRange(utf8); break;
                case int number: Integer(5, (ulong)number); break;
                case uint number: Integer(6, number); break;
                case ulong number: Integer(9, number); break;
                case double number:
                    Header(3, 8); var data = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(data, number); bytes.AddRange(data); break;
                case Dictionary<string, object> map:
                    Header(7, map.Count); foreach (var pair in map) { Write(pair.Key); Write(pair.Value); } break;
                case object[] array:
                    Header(11, array.Length); foreach (var item in array) Write(item); break;
                default: throw new InvalidOperationException("Unsupported controlled MMDB fixture value.");
            }
        }
    }
}
