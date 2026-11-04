namespace IpLocation.Api.Models.Providers;

/// <summary>启用与技术就绪独立；历史许可字段仅兼容旧合同，不参与启用、默认库、上传或查询判断。</summary>
public sealed record ProviderDescriptor(
    string Id,
    string Name,
    string Status,
    bool SupportsIpv4,
    bool SupportsIpv6,
    bool Enabled,
    bool LicenseConfirmed,
    bool Ready,
    string? Version,
    string Message,
    string LicenseUrl,
    string Attribution,
    string? LicenseReviewMode = null);
