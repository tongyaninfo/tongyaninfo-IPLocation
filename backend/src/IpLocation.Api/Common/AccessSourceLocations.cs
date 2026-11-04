using System.Text.Json;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Common;

/// <summary>定位快照只接受固定来源和有限字段；旧空值及损坏JSON不影响整页访问记录读取。</summary>
public static class AccessSourceLocations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };
    private static readonly HashSet<string> Statuses = ["success", "notFound", "special", "unsupported", "unavailable", "disabled", "notReady", "timeout", "invalid", "cancelled"];
    public const int MaxJsonCharacters = 8192;

    public static AccessSourceLocation? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonCharacters) return null;
        try
        {
            var value = JsonSerializer.Deserialize<AccessSourceLocation>(json, Json);
            return IsValid(value) ? value! with { Provider = ProviderCodes.Cz88 } : null;
        }
        catch (JsonException) { return null; }
    }
    public static string? Write(AccessSourceLocation? value)
    {
        if (!IsValid(value)) return null;
        var json = JsonSerializer.Serialize(value! with { Provider = ProviderCodes.Cz88 }, Json);
        if (json.Length <= MaxJsonCharacters) return json;
        // 中文、引号等转义可能放大JSON；缩短文本后仍保留来源与明确状态。
        var compact = value! with { Provider = ProviderCodes.Cz88, Country = Limit(value.Country, 64), Province = Limit(value.Province, 64),
            City = Limit(value.City, 64), Isp = Limit(value.Isp, 128), RawLocation = Limit(value.RawLocation, 512) };
        json = JsonSerializer.Serialize(compact, Json);
        return json.Length <= MaxJsonCharacters ? json : null;
    }
    private static bool IsValid(AccessSourceLocation? value) => value is not null &&
        value.Provider is not null && ProviderCodes.Public(value.Provider) == ProviderCodes.Cz88 && value.Status is not null && Statuses.Contains(value.Status) &&
        Safe(value.Version, 128) && Safe(value.Country, 256) && Safe(value.Province, 256) && Safe(value.City, 256) && Safe(value.Isp, 512) && Safe(value.RawLocation, 1024);
    private static bool Safe(string? value, int maxLength) => value is null || value.Length <= maxLength && !value.Any(char.IsControl);

    public static AccessSourceLocation FromResult(string? version, string? region, string? isp)
    {
        var raw = Limit(region, 1024);
        string? country = null, province = null, city = null;
        if (!string.IsNullOrWhiteSpace(region))
        {
            // 只采用来源原文明确的中文区域分隔，不按空格、拼音、行政区后缀或第三方词典猜造。
            var parts = region.Split(['–', '—'], StringSplitOptions.TrimEntries);
            if (parts.Length is >= 2 and <= 4 && parts.All(part => part.Length > 0))
            {
                country = Limit(parts[0], 256);
                province = Limit(parts[1], 256);
                // 四段地区可能包含郡县；仅有明确三级时填写城市，额外层级继续保留原文。
                if (parts.Length == 3) city = Limit(parts[2], 256);
            }
        }
        return new(ProviderCodes.Cz88, Limit(version, 128), "success", country, province, city, Limit(isp, 512), raw);
    }
    private static string? Limit(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Any(char.IsControl)) return null;
        return value.Length <= length ? value : value[..length];
    }
}
