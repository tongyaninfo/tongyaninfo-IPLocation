using System.Text;
using System.Text.Json;
using IpLocation.Api.Models.Responses;
using Microsoft.Extensions.Primitives;

namespace IpLocation.Api.Common;

/// <summary>采集有界、非秘密诊断头及脱敏路径；不保留 HttpContext、可变头集合或任意凭据头。</summary>
public static class AccessRequestHeaders
{
    public const int MaxHeaders = 32;
    public const int MaxValueCharacters = 2048;
    public const int MaxSnapshotBytes = 8 * 1024;
    public const int MaxStoredSnapshotBytes = MaxSnapshotBytes + AccessRequestPaths.MaxEncodedBytes + 128;
    private const int MaxValuesPerHeader = 8;
    private static readonly string[] AllowedNames = [
        "user-agent", "referer", "host", "origin", "accept", "accept-language", "accept-encoding",
        "content-type", "content-length", "cache-control", "sec-ch-ua", "sec-ch-ua-mobile",
        "sec-ch-ua-platform", "sec-ch-ua-platform-version", "sec-ch-ua-model", "sec-ch-ua-full-version",
        "sec-ch-ua-full-version-list", "sec-ch-ua-arch", "sec-ch-ua-bitness", "sec-ch-ua-wow64",
        "sec-fetch-site", "sec-fetch-mode", "sec-fetch-user", "sec-fetch-dest", "x-request-id", "traceparent",
        "pragma", "upgrade-insecure-requests", "dnt", "priority"
    ];
    private static readonly HashSet<string> Allowed = new(AllowedNames, StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    private static readonly int EnvelopeBytes = Encoding.UTF8.GetByteCount("{\"headers\":{},\"truncated\":false}");

    public static string? Capture(IHeaderDictionary headers, string? requestPath = null)
    {
        try
        {
            var path = AccessRequestPaths.Normalize(requestPath, out var pathTruncated);
            return JsonSerializer.Serialize(Build(headers, false) with { RequestPath = path, RequestPathTruncated = pathTruncated }, JsonOptions);
        }
        // 普通访问不能因为诊断元数据采集失败而改变响应，null 表示此条未采集。
        catch (Exception) { return null; }
    }

    /// <summary>历史 null、损坏 JSON 或错误结构不会阻断整页读取；读取时仍执行相同字段与长度限制。</summary>
    public static RequestHeadersSnapshot? Read(string? json)
    {
        if (json is null || json.Length > MaxStoredSnapshotBytes || Encoding.UTF8.GetByteCount(json) > MaxStoredSnapshotBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("headers", out var headers) ||
                headers.ValueKind != JsonValueKind.Object || !root.TryGetProperty("truncated", out var truncated) ||
                truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var values = new HeaderDictionary();
            var incomplete = truncated.GetBoolean();
            foreach (var property in headers.EnumerateObject())
            {
                if (!Allowed.Contains(property.Name)) { incomplete = true; continue; }
                var name = property.Name.ToLowerInvariant();
                if (values.ContainsKey(name) || property.Value.ValueKind != JsonValueKind.Array) return null;
                var entries = new List<string>();
                foreach (var value in property.Value.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String) return null;
                    if (entries.Count == MaxValuesPerHeader) { incomplete = true; break; }
                    entries.Add(value.GetString()!);
                }
                values[name] = new StringValues(entries.ToArray());
            }
            var pathTruncated = false;
            var path = root.TryGetProperty("requestPath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
                ? AccessRequestPaths.Normalize(pathElement.GetString(), out pathTruncated) : null;
            if (path is not null)
                pathTruncated |= root.TryGetProperty("requestPathTruncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            return Build(values, incomplete) with { RequestPath = path, RequestPathTruncated = pathTruncated };
        }
        catch (Exception) { return null; }
    }

    /// <summary>后台持久化再次规范化，未知/损坏的诊断快照不导致整批访问丢失。</summary>
    public static string? Normalize(string? json) => Read(json) is { } snapshot ? JsonSerializer.Serialize(snapshot, JsonOptions) : null;

    private static RequestHeadersSnapshot Build(IHeaderDictionary source, bool truncated)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var bytes = EnvelopeBytes; // 为 false 预留字节，true 更短；计入 JSON 转义、引号及数组分隔符。
        foreach (var name in AllowedNames)
        {
            if (!source.TryGetValue(name, out var incoming) || incoming.Count == 0) continue;
            if (result.Count >= MaxHeaders) { truncated = true; break; }
            if (incoming.Count > MaxValuesPerHeader) truncated = true;
            var values = new List<string>();
            var headerBytes = name.Length + 5 + (result.Count == 0 ? 0 : 1); // "name":[] 与前置逗号。
            if (bytes + headerBytes + 2 > MaxSnapshotBytes) { truncated = true; break; }
            for (var index = 0; index < Math.Min(incoming.Count, MaxValuesPerHeader); index++)
            {
                var value = Clean(incoming[index] ?? "", ref truncated);
                if (name is "referer" or "origin")
                {
                    value = SafeUrl(value, name == "origin");
                    if (value is null) { truncated = true; continue; }
                }
                var syntaxBytes = 2 + (values.Count == 0 ? headerBytes : 1);
                var valueBudget = MaxSnapshotBytes - bytes - syntaxBytes;
                if (valueBudget < 0) { truncated = true; break; }
                var encodedBytes = JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;
                if (encodedBytes > valueBudget)
                {
                    truncated = true;
                    value = FitEncodedValue(value, valueBudget);
                    encodedBytes = JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length;
                }
                var valueBytes = encodedBytes + syntaxBytes;
                values.Add(value); bytes += valueBytes;
            }
            if (values.Count > 0) result.Add(name, values.ToArray());
        }
        return new(result, truncated);
    }

    private static string FitEncodedValue(string value, int budget)
    {
        // 仅在极限值超过剩余预算时二分；普通请求不反复序列化整个快照。
        var low = 0; var high = value.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (JsonEncodedText.Encode(value[..middle]).EncodedUtf8Bytes.Length <= budget) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
        return value[..low];
    }

    private static string Clean(string value, ref bool truncated)
    {
        var take = Math.Min(value.Length, MaxValueCharacters);
        if (take != value.Length) truncated = true;
        var needsCleaning = false;
        for (var index = 0; index < take; index++)
            if (char.IsControl(value[index]) || char.IsSurrogate(value[index])) { needsCleaning = true; break; }
        if (!needsCleaning) return take == value.Length ? value : value[..take];
        var builder = new StringBuilder(take);
        for (var index = 0; index < take; index++)
        {
            var current = value[index];
            if (char.IsControl(current)) { truncated = true; continue; }
            if (char.IsHighSurrogate(current) && index + 1 < take && char.IsLowSurrogate(value[index + 1]))
            { builder.Append(current).Append(value[++index]); continue; }
            if (char.IsSurrogate(current)) { truncated = true; continue; }
            builder.Append(current);
        }
        return builder.ToString();
    }

    private static string? SafeUrl(string value, bool origin)
    {
        if (origin && value == "null") return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        // Referer 保留来源路径；查询串、片段和 userinfo 不进入访问日志。
        return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
    }
}
