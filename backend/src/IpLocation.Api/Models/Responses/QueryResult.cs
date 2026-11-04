namespace IpLocation.Api.Models.Responses;

/// <summary>一次逻辑查询以及每个来源的独立状态。</summary>
public sealed record QueryResult(string Ip, string AddressType, string Status, IReadOnlyList<SourceResult> Sources);

public sealed record SourceResult(
    string Id,
    string Name,
    string Status,
    string? Version,
    IpFields Fields,
    long ElapsedMs,
    string Message,
    string LicenseUrl,
    string Attribution);
