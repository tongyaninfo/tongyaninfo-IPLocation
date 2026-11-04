using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>管理员账户；账户须显式创建，不提供默认用户或密码。</summary>
public sealed class AdminAccount
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    public string Account { get; set; } = string.Empty;
    public string NormalizedAccount { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    [JsonIgnore]
    public string PasswordHash { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    /// <summary>账号状态或凭据变更时递增，用于会话失效与乐观并发校验。</summary>
    public long SecurityVersion { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
