using System.Net;

namespace IpLocation.Api.Service;

public interface IClientIpService
{
    /// <summary>仅读取已由可信代理中间件处理的连接地址，返回规范化 IP。</summary>
    IPAddress? GetClientIp(HttpContext context);

    string? GetClientIpString(HttpContext context) => GetClientIp(context)?.ToString();
}
