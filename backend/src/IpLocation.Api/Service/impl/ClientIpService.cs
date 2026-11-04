using System.Net;
using IpLocation.Api.Common;

namespace IpLocation.Api.Service.impl;

public sealed class ClientIpService : IClientIpService
{
    public IPAddress? GetClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is null ? null : IpAddressParser.Normalize(address);
    }
}
