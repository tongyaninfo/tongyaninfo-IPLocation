using System.Text.Json;
using System.Text.Json.Nodes;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Common;

/// <summary>仅在内存清洗 JSON，不读取流或写存储；预算包含属性、分隔符及 JSON 转义后的 UTF-8 字节。</summary>
public static class McpPayloads
{
    public const int MaxInputBytes = 8 * 1024;
    public const int MaxOutputBytes = 32 * 1024;
    public const int MaxDepth = 12;
    public const int MaxContainerItems = 64;
    public const int MaxNodes = 1024;
    public const int MaxStringCharacters = 4096;
    public const string RedactedValue = "[REDACTED]";
    private const int MaxPropertyCharacters = 128;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = MaxDepth + 4 };
    private static readonly HashSet<string> DiagnosticNames = ["details", "stack", "stacktrace", "stackpath", "exception", "innerexception", "filepath", "storagepath", "databasepath"];
    private static readonly string[] SensitiveParts = ["password", "passwd", "token", "secret", "authorization", "cookie", "connectionstring", "credential", "apikey", "privatekey", "sessionkey", "sessionid", "accesskey", "signingkey", "encryptionkey"];

    public static McpPayloadSnapshot Capture(JsonElement? value, int maxBytes)
    {
        if (!value.HasValue || value.Value.ValueKind == JsonValueKind.Undefined) return new(null, false);
        if (maxBytes is < 16 or > MaxOutputBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        try
        {
            var state = new CaptureState();
            var result = Build(value.Value, maxBytes, 0, state);
            var json = result.Node?.ToJsonString(JsonOptions) ?? "null";
            // Build 的字节预算与最终表示相同；再次防御检查，任何边界故障不影响公开查询。
            if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes) return new(JsonSerializer.SerializeToElement("[TRUNCATED]"), true);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth + 2 });
            return new(document.RootElement.Clone(), state.Truncated);
        }
        catch (Exception) { return new(null, true); }
    }

    private static BuiltValue Build(JsonElement value, int budget, int depth, CaptureState state, JsonElement? duplicateOf = null)
    {
        if (depth >= MaxDepth || ++state.Nodes > MaxNodes)
        {
            state.Truncated = true;
            return Marker(budget);
        }
        return value.ValueKind switch
        {
            JsonValueKind.Object => BuildObject(value, budget, depth, state),
            JsonValueKind.Array => BuildArray(value, budget, depth, state, duplicateOf),
            JsonValueKind.String => BuildString(value.GetString()!, budget, depth, state),
            JsonValueKind.True => new(JsonValue.Create(true), 4),
            JsonValueKind.False => budget >= 5 ? new(JsonValue.Create(false), 5) : TruncatedNull(state),
            JsonValueKind.Number => Number(value, budget, state),
            _ => new(null, 4)
        };
    }

    private static BuiltValue BuildObject(JsonElement source, int budget, int depth, CaptureState state)
    {
        var result = new JsonObject();
        var bytes = 2;
        var count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rpcError = source.TryGetProperty("code", out var errorCode) && errorCode.ValueKind == JsonValueKind.Number &&
            source.TryGetProperty("message", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String;
        JsonElement? structured = source.TryGetProperty("structuredContent", out var content) &&
            content.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? content : null;
        foreach (var property in source.EnumerateObject())
        {
            if (++count > MaxContainerItems) { state.Truncated = true; break; }
            var name = property.Name;
            if (name.Length > MaxPropertyCharacters || name.Any(char.IsControl) || !seen.Add(name)) { state.Truncated = true; continue; }
            var normalized = NormalizeName(name);
            if (name.Equals("_meta", StringComparison.OrdinalIgnoreCase) || DiagnosticNames.Contains(normalized) ||
                (rpcError && name is not ("code" or "message")))
            {
                state.Truncated = true;
                continue;
            }
            var prefix = EncodedStringBytes(name) + 1 + (result.Count == 0 ? 0 : 1);
            var available = budget - bytes - prefix;
            if (available < 4) { state.Truncated = true; break; }
            BuiltValue child;
            if (IsSensitive(normalized))
            {
                state.Truncated = true;
                child = EncodedStringBytes(RedactedValue) <= available
                    ? new(JsonValue.Create(RedactedValue), EncodedStringBytes(RedactedValue)) : new(null, 4);
            }
            else child = Build(property.Value, available, depth + 1, state,
                name == "content" && property.Value.ValueKind == JsonValueKind.Array ? structured : null);
            result.Add(name, child.Node);
            bytes += prefix + child.Bytes;
        }
        return new(result, bytes);
    }

    private static BuiltValue BuildArray(JsonElement source, int budget, int depth, CaptureState state, JsonElement? duplicateOf)
    {
        var result = new JsonArray();
        var bytes = 2;
        var count = 0;
        foreach (var item in source.EnumerateArray())
        {
            if (++count > MaxContainerItems) { state.Truncated = true; break; }
            // MCP 文本回退与 structuredContent 表达相同 JSON 时只存后者，避免放大日志和重复披露。
            if (duplicateOf.HasValue && IsEquivalentText(item, duplicateOf.Value)) continue;
            var prefix = result.Count == 0 ? 0 : 1;
            var available = budget - bytes - prefix;
            if (available < 4) { state.Truncated = true; break; }
            var child = Build(item, available, depth + 1, state);
            result.Add(child.Node);
            bytes += prefix + child.Bytes;
        }
        return new(result, bytes);
    }

    private static BuiltValue BuildString(string value, int budget, int depth, CaptureState state)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            // 与安全 Referer 快照保持相同语义；非法 IP 参数中的 URL 也不能带入账户、查询串或片段。
            var safeUrl = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            if (!string.Equals(value, safeUrl, StringComparison.Ordinal)) state.Truncated = true;
            value = safeUrl;
        }
        if (value is RedactedValue or "[TRUNCATED]" or "[OMITTED_JSON_TEXT]" or "[TRUNCATED_JSON_TEXT]")
            return StringValue(value, budget, state);
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[')
        {
            // JSON 包在 content.text 或其他字符串中也要清洗；不能借字符串绕过敏感属性名规则。
            if (value.Length > MaxStringCharacters)
            {
                state.Truncated = true;
                return StringValue("[OMITTED_JSON_TEXT]", budget, state);
            }
            try
            {
                using var nested = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = MaxDepth });
                var clean = Build(nested.RootElement, Math.Min(budget, MaxOutputBytes), depth + 1, state);
                var json = clean.Node?.ToJsonString(JsonOptions) ?? "null";
                if (EncodedStringBytes(json) <= budget) return new(JsonValue.Create(json), EncodedStringBytes(json));
                state.Truncated = true;
                return StringValue("[TRUNCATED_JSON_TEXT]", budget, state);
            }
            catch (JsonException)
            {
                // 不保留无法完成清洗的 JSON 文本片段，避免半段 token/password 等值原样留下。
                state.Truncated = true;
                return StringValue("[OMITTED_JSON_TEXT]", budget, state);
            }
        }
        return StringValue(value, budget, state);
    }

    private static BuiltValue StringValue(string value, int budget, CaptureState state)
    {
        var limit = Math.Min(value.Length, MaxStringCharacters);
        if (limit != value.Length) state.Truncated = true;
        // 二分只计算标量的转义长度，不反复序列化整份入出参。
        var low = 0;
        var high = limit;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (EncodedStringBytes(value.AsSpan(0, middle)) <= budget) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
        if (low != value.Length) state.Truncated = true;
        var clean = low == value.Length ? value : value[..low];
        return new(JsonValue.Create(clean), EncodedStringBytes(clean));
    }

    private static BuiltValue Number(JsonElement value, int budget, CaptureState state)
    {
        var raw = value.GetRawText();
        if (raw.Length > 128 || raw.Length > budget) return TruncatedNull(state);
        return new(JsonValue.Create(value.Clone()), raw.Length);
    }

    private static bool IsEquivalentText(JsonElement block, JsonElement structured)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "text" ||
            !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return false;
        var value = text.GetString();
        if (value is null || value.Length > MaxOutputBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return JsonElement.DeepEquals(document.RootElement, structured);
        }
        catch (JsonException) { return false; }
    }

    private static string NormalizeName(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    /// <summary>HTTP 查询串与 MCP 快照复用凭据字段判定，避免两条日志管线的脱敏规则漂移。</summary>
    public static bool IsSensitiveName(string name) => IsSensitive(NormalizeName(name));
    private static bool IsSensitive(string name) => name is "pwd" or "key" || SensitiveParts.Any(name.Contains);
    private static int EncodedStringBytes(string value) => EncodedStringBytes(value.AsSpan());
    private static int EncodedStringBytes(ReadOnlySpan<char> value)
    {
        // 二分前缀可能恰好切在 emoji 的高代理项后，计算时退回完整码点，不能让合法 Unicode 导致整份快照丢失。
        if (!value.IsEmpty && char.IsHighSurrogate(value[^1])) value = value[..^1];
        return JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length + 2;
    }
    private static BuiltValue Marker(int budget) => EncodedStringBytes("[TRUNCATED]") <= budget
        ? new(JsonValue.Create("[TRUNCATED]"), EncodedStringBytes("[TRUNCATED]")) : new(null, 4);
    private static BuiltValue TruncatedNull(CaptureState state) { state.Truncated = true; return new(null, 4); }
    private sealed class CaptureState { public bool Truncated; public int Nodes; }
    private sealed record BuiltValue(JsonNode? Node, int Bytes);
}
