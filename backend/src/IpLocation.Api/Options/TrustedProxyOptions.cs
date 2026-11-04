namespace IpLocation.Api.Options;

/// <summary>空配置不处理转发头；不沿用框架默认的回环代理信任。</summary>
public sealed class TrustedProxyOptions
{
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownIPNetworks { get; set; } = [];
    public int ForwardLimit { get; set; } = 1;
}
