namespace IpLocation.Api.Models.Responses;

/// <summary>统一展示字段；未提供的内容保留 null，不用其他来源补造。</summary>
public sealed record IpFields
{
    public string? CountryCode { get; init; }
    public string? Country { get; init; }
    public string? ContinentCode { get; init; }
    public string? Continent { get; init; }
    public string? Region { get; init; }
    public string? City { get; init; }
    public string? Isp { get; init; }
    public string? Asn { get; init; }
    public string? AsName { get; init; }
    public string? AsDomain { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? PostalCode { get; init; }
    public string? TimeZone { get; init; }
}
