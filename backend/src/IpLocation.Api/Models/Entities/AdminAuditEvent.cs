using System.Net;
using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>管理关键操作的可靠审计；身份未知的登录失败可没有 ActorId。</summary>
public sealed class AdminAuditEvent
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? ActorId { get; set; }
    public IPAddress? SourceAddress { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string DifferenceJson { get; set; } = "{}";
    public string Result { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}
