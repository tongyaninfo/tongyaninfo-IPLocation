using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>非敏感设置；秘密、Token 与密码必须使用获准的私有配置机制。</summary>
public sealed class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? UpdatedById { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
