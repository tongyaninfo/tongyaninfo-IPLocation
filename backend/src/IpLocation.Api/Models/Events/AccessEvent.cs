namespace IpLocation.Api.Models.Events;

/// <summary>公共访问元数据；诊断快照包含脱敏路径及查询串，不包含凭据、请求体或响应内容。</summary>
public sealed record AccessEvent(
    DateTimeOffset OccurredAtUtc,
    string TraceId,
    string RouteCategory,
    string Method,
    string Protocol,
    string Format,
    string? Database,
    string? DatabaseVersion,
    int StatusCode,
    double ElapsedMilliseconds,
    bool IsCancelled,
    string? SourceAddress,
    string ClientCategory,
    string? RequestHeadersJson = null,
    string? SourceLocationJson = null,
    string? McpContextJson = null);
