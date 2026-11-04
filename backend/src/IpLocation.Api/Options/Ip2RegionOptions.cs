namespace IpLocation.Api.Options;

/// <summary>官方仓库自带 v3 XDB 的只读配置；IPv4、IPv6 文件分别验证和查询。</summary>
public sealed class Ip2RegionOptions
{
    public bool Enabled { get; set; }
    /// <summary>仅兼容旧配置，不参与启停、文件读取或查询判断。</summary>
    public bool LicenseConfirmed { get; set; }
    public string Ipv4DatabasePath { get; set; } = "";
    public string Ipv6DatabasePath { get; set; } = "";
    /// <summary>单个 XDB 的读取大小上限，避免错误文件造成无界资源占用。</summary>
    public long MaxDatabaseBytes { get; set; } = 268_435_456;
}
