using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Responses;

/// <summary>允许采集的请求头和路径元数据；复用既有 JSON 列，历史未采集路径保持 null。</summary>
public sealed record RequestHeadersSnapshot(IReadOnlyDictionary<string, string[]> Headers, bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RequestPathTruncated = false);
