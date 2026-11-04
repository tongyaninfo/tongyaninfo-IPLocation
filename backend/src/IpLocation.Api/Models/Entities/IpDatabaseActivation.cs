using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>按来源和地址族维护活动指针；Revision 必须随受审事务更新。</summary>
public sealed class IpDatabaseActivation
{
    public string ProviderCode { get; set; } = string.Empty;
    public string AddressFamily { get; set; } = string.Empty;
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long VersionId { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? UpdatedById { get; set; }
}
