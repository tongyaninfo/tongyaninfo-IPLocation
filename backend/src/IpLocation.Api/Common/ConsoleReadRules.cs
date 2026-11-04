using System.Text.Json;
using IpLocation.Api.Models.Requests;

namespace IpLocation.Api.Common;

/// <summary>后台只读请求的边界规则及审计二次脱敏，不接触数据库或私有配置。</summary>
public static class ConsoleReadRules
{
    private static readonly HashSet<string> AuditKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "before", "after", "query", "enabled", "defaultDatabase", "providerTimeoutMs", "totalTimeoutMs",
        "maxConcurrentSources", "allowedNetworks", "networks", "network", "note", "providerCode", "provider", "providerIds", "errorCode",
        "versionId", "previousVersionId", "revision", "fileFormat", "addressFamily", "sha256", "byteSize",
        "status", "account", "accountName", "oldAccount", "newAccount", "securityVersion",
        "previousSecurityVersion", "newSecurityVersion", "version", "format", "sizeBytes", "licenseConfirmed",
        "credentialsReset", "sessionsRevoked",
        "retentionDays", "recentEventRetentionDays", "minuteRetentionDays", "retainFullSourceIp", "reasonCode", "errorCode", "provider",
        "basic", "accessLogRetentionDays", "sessionMinutes", "queueCapacity"
    };

    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) NormalizeWindow(ConsoleReadWindowRequest request,
        DateTimeOffset now, int? retentionDays, bool completeMinutes = false)
    {
        now = now.ToUniversalTime();
        var to = (request.ToUtc ?? now).ToUniversalTime();
        if (to > now) to = now;
        var from = (request.FromUtc ?? to.AddDays(-1)).ToUniversalTime();
        if (from >= to || to - from > TimeSpan.FromDays(31))
            throw new ConsoleReadValidationException("请选择不超过31天的有效时间范围。");
        var cutoff = ConsoleBasicSettingsRules.ReadCutoff(now, retentionDays);
        if (cutoff.HasValue && to <= cutoff.Value) throw new ConsoleReadValidationException("读取时间范围已超出可读保留期。");
        if (cutoff.HasValue && from < cutoff.Value) from = cutoff.Value;
        if (completeMinutes)
        {
            from = ToMinute(from);
            var minuteTo = ToMinute(to);
            to = minuteTo < to ? minuteTo.AddMinutes(1) : minuteTo;
        }
        return (from, to);
    }

    public static int PageOffset(int page, int pageSize)
    {
        if (page is < 1 or > 1_000 || pageSize is < 1 or > 100)
            throw new ConsoleReadValidationException("页码须为1至1000，每页须为1至100条。");
        return (page - 1) * pageSize;
    }

    public static string? Filter(string? value, int maxLength)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > maxLength || value.Any(char.IsControl)) throw new ConsoleReadValidationException("筛选条件无效或超出长度限制。");
        return value;
    }

    public static double? Percentile95(IReadOnlyList<long> counts)
    {
        var bounds = AccessLogPersistencePlan.LatencyUpperBoundsMilliseconds;
        if (counts.Count != bounds.Count + 1 || counts.Any(count => count < 0)) return null;
        var total = counts.Sum();
        if (total == 0) return null;
        var target = (long)Math.Ceiling(total * .95);
        long cumulative = 0;
        for (var index = 0; index < counts.Count; index++)
        {
            cumulative += counts[index];
            if (cumulative >= target) return index < bounds.Count ? bounds[index] : null;
        }
        return null;
    }

    public static IReadOnlyDictionary<string, JsonElement> SanitizeDifference(string json)
    {
        if (json.Length > 32_768) return new Dictionary<string, JsonElement>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new Dictionary<string, JsonElement>();
            return ReadAllowedObject(document.RootElement, 0);
        }
        catch (JsonException) { return new Dictionary<string, JsonElement>(); }
    }

    private static Dictionary<string, JsonElement> ReadAllowedObject(JsonElement element, int depth)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (depth >= 4) return result;
        foreach (var property in element.EnumerateObject().Take(64))
        {
            if (!AuditKeys.Contains(property.Name)) continue;
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Object)
                result[property.Name] = JsonSerializer.SerializeToElement(ReadAllowedObject(value, depth + 1));
            else if (value.ValueKind == JsonValueKind.Array)
            {
                // 不透传任意嵌套对象/数组；白名单和地址族等简单值可保留。
                if (value.GetArrayLength() <= 128 && value.EnumerateArray().All(IsSafeScalar))
                    result[property.Name] = value.Clone();
            }
            else if (IsSafeScalar(value)) result[property.Name] = value.Clone();
        }
        return result;
    }

    private static bool IsSafeScalar(JsonElement value) => value.ValueKind is JsonValueKind.Number or
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 512 });

    private static DateTimeOffset ToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero);
}

/// <summary>只包含服务端固定的请求校验提示；系统异常正文不通过此类型返回客户端。</summary>
public sealed class ConsoleReadValidationException(string message) : ArgumentException(message);
