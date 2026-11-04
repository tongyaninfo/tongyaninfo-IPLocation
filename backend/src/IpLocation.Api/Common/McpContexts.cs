using System.Text;
using System.Text.Json;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Common;

/// <summary>仅序列化明确声明的 MCP 字段；不复制请求正文、凭据或任意 _meta，读回时再次应用相同边界。</summary>
public static class McpContexts
{
    public const int MaxSnapshotBytes = 48 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = McpPayloads.MaxDepth + 4 };
    private static readonly HashSet<string> Databases = ["geolite2", "ip2location", "dbip", "ipinfo", "ip2region", ProviderCodes.Cz88, "all"];

    public static string? Write(McpCallContext? context)
    {
        if (context is null) return null;
        try
        {
            var clean = Normalize(context);
            var json = JsonSerializer.Serialize(clean, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= MaxSnapshotBytes) return json;
            // 极限预算优先保留协议身份、结果与入参，不因大出参把整条调用上下文变成空值。
            clean = clean with { Output = null, OutputTruncated = true, Truncated = true };
            json = JsonSerializer.Serialize(clean, JsonOptions);
            return Encoding.UTF8.GetByteCount(json) <= MaxSnapshotBytes ? json : null;
        }
        catch (Exception) { return null; }
    }

    public static McpCallContext? Read(string? json)
    {
        if (json is null || json.Length > MaxSnapshotBytes || Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes) return null;
        try
        {
            // 强类型反序列化天然丢弃未登记属性；重复字段拒绝，避免存储与界面读取产生歧义。
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = McpPayloads.MaxDepth + 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || HasDuplicateProperties(document.RootElement)) return null;
            var context = JsonSerializer.Deserialize<McpCallContext>(json, JsonOptions);
            return context is null ? null : Normalize(context);
        }
        catch (Exception) { return null; }
    }

    public static string? Normalize(string? json) => Write(Read(json));

    /// <summary>握手和工具目录不计定位查询；仅已识别的两个定位工具属于逻辑查询。</summary>
    public static bool IsLocationQuery(string? json)
    {
        if (json is null || json.Length > MaxSnapshotBytes || Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes) return false;
        try
        {
            // 分钟计数只读取两个固定字段，不为每次统计重新清洗和复制完整入出参。
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = McpPayloads.MaxDepth + 4 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && !HasDuplicateProperties(root) &&
                   root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.ValueEquals("tools/call") &&
                   root.TryGetProperty("toolName", out var tool) && tool.ValueKind == JsonValueKind.String &&
                   (tool.ValueEquals("query_ip") || tool.ValueEquals("query_my_ip"));
        }
        catch (JsonException) { return false; }
    }

    private static McpCallContext Normalize(McpCallContext value)
    {
        var truncated = value.Truncated;
        var protocol = Clean(value.ProtocolVersion, 32, ref truncated);
        var method = Clean(value.Method, 64, ref truncated);
        var requestId = Clean(value.RequestId, 128, ref truncated);
        var tool = Clean(value.ToolName, 64, ref truncated);
        var name = Clean(value.ClientName, 128, ref truncated);
        var version = Clean(value.ClientVersion, 64, ref truncated);
        string? ip = null;
        if (value.Ip is not null)
        {
            if (IpAddressParser.TryParse(value.Ip, out var address)) ip = address.ToString();
            else truncated = true;
        }
        var database = Database(value.Database, ref truncated);
        var code = Clean(value.ResultCode, 64, ref truncated);
        var status = Clean(value.ResultStatus, 32, ref truncated);
        var sources = new List<McpSourceStatus>();
        if (value.Sources is { } incoming)
        {
            if (incoming.Count > 6) truncated = true;
            foreach (var source in incoming.Take(6))
            {
                if (source is null) { truncated = true; continue; }
                var id = Database(source.Id, ref truncated);
                if (id is null || id == "all") { truncated = true; continue; }
                var sourceVersion = Clean(source.Version, 128, ref truncated);
                var sourceStatus = Clean(source.Status, 32, ref truncated);
                if (sourceStatus is null) { truncated = true; continue; }
                sources.Add(new(id, sourceVersion, sourceStatus));
            }
        }
        var inputCaptured = value.InputCaptured || value.Input.HasValue;
        var outputCaptured = value.OutputCaptured || value.Output.HasValue;
        // Nullable<JsonElement> 反序列化 JSON null 后没有 HasValue，独立采集标记用于恢复实际的 JSON null。
        var jsonNull = JsonSerializer.SerializeToElement<object?>(null);
        var input = McpPayloads.Capture(value.Input ?? (inputCaptured ? jsonNull : null), McpPayloads.MaxInputBytes);
        var output = McpPayloads.Capture(value.Output ?? (outputCaptured ? jsonNull : null), McpPayloads.MaxOutputBytes);
        var inputTruncated = value.InputTruncated || input.Truncated;
        var outputTruncated = value.OutputTruncated || output.Truncated;
        return new(protocol, method, requestId, tool, name, version, ip, database, code, status,
            value.IsError, sources, truncated || inputTruncated || outputTruncated, input.Value, output.Value,
            inputTruncated, outputTruncated, inputCaptured, outputCaptured);
    }

    private static string? Database(string? value, ref bool truncated)
    {
        if (value is null) return null;
        if (value.Length > 32) { truncated = true; return null; }
        var code = ProviderCodes.Public(value.Trim().ToLowerInvariant());
        if (Databases.Contains(code)) return code;
        truncated = true;
        return null;
    }

    private static string? Clean(string? value, int limit, ref bool truncated)
    {
        if (value is null) return null;
        var builder = new StringBuilder(Math.Min(value.Length, limit));
        foreach (var character in value.Take(limit))
        {
            if (char.IsControl(character) || char.IsSurrogate(character)) { truncated = true; continue; }
            builder.Append(character);
        }
        if (value.Length > limit) truncated = true;
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
