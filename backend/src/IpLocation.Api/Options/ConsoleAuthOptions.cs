namespace IpLocation.Api.Options;

/// <summary>管理员认证运行配置；密码只从获准的私有文件交付，不写入源码默认值。</summary>
public sealed class ConsoleAuthOptions
{
    public const string SectionName = "ConsoleAuth";
    public string KeyRingPath { get; set; } = string.Empty;
    public int SessionMinutes { get; set; } = 480;
}
