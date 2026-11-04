namespace IpLocation.Api.Options;

/// <summary>只读 City 文件的非敏感配置；实际路径由获准的本地运行配置提供。</summary>
public sealed class GeoLite2Options
{
    public bool Enabled { get; set; }
    /// <summary>兼容旧配置，不参与启用、读取或查询判断。</summary>
    public bool LicenseConfirmed { get; set; }
    public string DatabasePath { get; set; } = "";

    /// <summary>
    /// 兼容旧配置；不再用于按许可或数据年龄自动停用来源。
    /// </summary>
    public int MaxAgeDays { get; set; } = 30;
}
