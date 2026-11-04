namespace IpLocation.Api.Options;

/// <summary>白名单是入口约束，不替代后续管理员身份与写请求防伪。</summary>
public sealed class ConsoleOptions
{
    public string[] AllowedNetworks { get; set; } = [];
}
