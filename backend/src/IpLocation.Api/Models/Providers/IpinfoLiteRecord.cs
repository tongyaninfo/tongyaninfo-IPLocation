using MaxMind.Db;

namespace IpLocation.Api.Models.Providers;

/// <summary>IPinfo Lite 的扁平 MMDB schema，不套用 MaxMind City 的嵌套结构。</summary>
public sealed class IpinfoLiteRecord
{
    [MapKey("country_code")]
    public string? CountryCode { get; set; }
    [MapKey("country")]
    public string? Country { get; set; }
    [MapKey("continent_code")]
    public string? ContinentCode { get; set; }
    [MapKey("continent")]
    public string? Continent { get; set; }
    [MapKey("asn")]
    public string? Asn { get; set; }
    [MapKey("as_name")]
    public string? AsName { get; set; }
    [MapKey("as_domain")]
    public string? AsDomain { get; set; }
}
