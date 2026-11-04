namespace IpLocation.Api.Options;

/// <summary>持久化默认关闭；凭据通过获准的运行配置提供，仓库不包含连接秘密。</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public bool Enabled { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 10;
    public string ConnectionStringName { get; set; } = "IpLocation";
}
