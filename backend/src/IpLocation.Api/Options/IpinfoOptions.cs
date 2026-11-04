namespace IpLocation.Api.Options;

/// <summary>IPinfo Lite 离线 MMDB 只读配置；查询不使用 Token 或在线 API。</summary>
public sealed class IpinfoOptions
{
    public bool Enabled { get; set; }
    /// <summary>仅兼容旧配置，不参与启停、文件读取或查询判断。</summary>
    public bool LicenseConfirmed { get; set; }
    public string DatabasePath { get; set; } = "";
}
