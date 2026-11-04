using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>不可变数据文件与授权依据；私有存储定位不能直接作为公共接口响应。</summary>
public sealed class IpDatabaseVersion
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileFormat { get; set; } = string.Empty;
    public string AddressFamily { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    [JsonIgnore]
    public string StoragePath { get; set; } = string.Empty;
    public string? OfficialVersion { get; set; }
    /// <summary>供应商仅提供日期时只保存日期，不编造精确发布时间。</summary>
    public DateOnly? PublishedOn { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string OfficialReleaseInfoJson { get; set; } = "{}";
    public string LicenseEvidenceJson { get; set; } = "{}";
    public bool LicenseConfirmed { get; set; }
    public string Status { get; set; } = "received";
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long CreatedById { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ValidatedAtUtc { get; set; }
}
