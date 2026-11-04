using System.Text;
using System.Text.Json;

namespace IpLocation.Api.Common;

/// <summary>保留请求路径及查询串的原始顺序和编码，只清洗凭据参数；不采集域名、请求体或响应。</summary>
public static class AccessRequestPaths
{
    public const int MaxEncodedBytes = 32 * 1024;

    public static string? Normalize(string? path, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(path) || path[0] != '/') return null;
        // JSON 编码至少与字符数等长，先限制处理窗口，避免异常请求占用无界内存。
        if (path.Length > MaxEncodedBytes)
        {
            path = path[..MaxEncodedBytes];
            truncated = true;
        }
        var cleaned = new StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            var current = path[index];
            if (char.IsControl(current)) { truncated = true; continue; }
            if (char.IsHighSurrogate(current) && index + 1 < path.Length && char.IsLowSurrogate(path[index + 1]))
            { cleaned.Append(current).Append(path[++index]); continue; }
            if (char.IsSurrogate(current)) { truncated = true; continue; }
            cleaned.Append(current);
        }
        path = cleaned.ToString();
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            var parts = path[(queryIndex + 1)..].Split('&');
            for (var index = 0; index < parts.Length; index++)
            {
                var equals = parts[index].IndexOf('=');
                if (equals < 0) continue;
                var name = Uri.UnescapeDataString(parts[index][..equals].Replace('+', ' '));
                var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                if (McpPayloads.IsSensitiveName(name) || normalized.Contains("session", StringComparison.Ordinal) ||
                    normalized.Contains("signature", StringComparison.Ordinal) || normalized is "auth" or "sign")
                    parts[index] = parts[index][..(equals + 1)] + "[REDACTED]";
            }
            path = path[..(queryIndex + 1)] + string.Join('&', parts);
        }
        if (JsonEncodedText.Encode(path).EncodedUtf8Bytes.Length <= MaxEncodedBytes) return path;
        truncated = true;
        var low = 0;
        var high = path.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var take = middle > 0 && char.IsHighSurrogate(path[middle - 1]) ? middle - 1 : middle;
            if (JsonEncodedText.Encode(path.AsSpan(0, take)).EncodedUtf8Bytes.Length <= MaxEncodedBytes) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(path[low - 1])) low--;
        return path[..low];
    }
}
