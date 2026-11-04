namespace IpLocation.Api.Options;

/// <summary>IP2Location DB11 MMDB 的只读配置；实际文件路径由获准的本地配置提供。</summary>
public sealed class Ip2LocationOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>仅兼容旧配置，不参与启停、文件读取或查询判断。</summary>
    public bool LicenseConfirmed { get; set; }
    public string DatabasePath { get; set; } = "";
    /// <summary>仅供保留的旧 CSV 读取器使用；MMDB 版本始终来自文件 metadata。</summary>
    public string? DataVersion { get; set; }
    /// <summary>旧 CSV 的可选内容指纹；MMDB 不使用此项，也不把指纹当作供应商发布日期。</summary>
    public bool UseContentHashAsVersion { get; set; }
    /// <summary>以下资源限制仅用于保留的旧 CSV 校验器，当前 MMDB 来源不建立 CSV 索引。</summary>
    public int MaxRecordBytes { get; set; } = 1_048_576;
    public int MaxIndexEntries { get; set; } = 10_000_000;
}
