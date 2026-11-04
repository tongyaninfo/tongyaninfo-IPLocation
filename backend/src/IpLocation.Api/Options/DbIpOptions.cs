namespace IpLocation.Api.Options;

/// <summary>DB-IP City Lite 只读配置；真实文件路径与启用选择由获准运行配置提供。</summary>
public sealed class DbIpOptions
{
    public bool Enabled { get; set; }
    /// <summary>仅兼容旧配置，不参与启停、文件读取或查询判断。</summary>
    public bool LicenseConfirmed { get; set; }
    public string DatabasePath { get; set; } = "";
}
