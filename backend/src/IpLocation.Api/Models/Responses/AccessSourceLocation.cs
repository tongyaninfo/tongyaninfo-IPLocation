namespace IpLocation.Api.Models.Responses;

/// <summary>后台消费者查询来源 IP 的定位快照；与用户输入的查询目标无关，空字段不补造。</summary>
public sealed record AccessSourceLocation(string Provider, string? Version, string Status,
    string? Country, string? Province, string? City, string? Isp, string? RawLocation);
