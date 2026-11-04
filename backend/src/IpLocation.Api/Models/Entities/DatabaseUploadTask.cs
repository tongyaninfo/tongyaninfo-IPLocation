using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>上传与校验任务元数据，不存储库文件正文或供应商原始错误信息。</summary>
public sealed class DatabaseUploadTask
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string State { get; set; } = "receiving";
    public long ReceivedBytes { get; set; }
    public string? Sha256 { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long? VersionId { get; set; }
    public string Phase { get; set; } = "receiving";
    public string? ErrorCode { get; set; }
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long OperatorId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
}
