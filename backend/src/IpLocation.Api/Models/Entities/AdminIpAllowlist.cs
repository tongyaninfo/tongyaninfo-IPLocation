using System.Net;
using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>后台入口允许网段；精确 IP 使用 /32 或 /128，空表不表示允许所有地址。</summary>
public sealed class AdminIpAllowlist
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    public IPNetwork Network { get; set; }
    public bool Enabled { get; set; }
    public string? Note { get; set; }
    /// <summary>受控本地初始化可没有已存在管理员，正常管理变更必须由服务填写操作者。</summary>
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? CreatedById { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? UpdatedById { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
