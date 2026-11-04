using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

public sealed class ConsoleAccessService(IOptionsMonitor<ConsoleOptions> options, IConsoleRuntimeSettings? runtime = null) : IConsoleAccessService
{
    public bool IsAllowed(IPAddress? address)
    {
        if (address is null) return false;
        address = IpAddressParser.Normalize(address);

        // 每次请求读取当前白名单；空列表或无效条目不能意外扩大允许范围。
        if (runtime is not null && !runtime.Current.IsReady) return false;
        foreach (var entry in runtime?.Current.AllowedNetworks ?? options.CurrentValue.AllowedNetworks)
        {
            if (IpAddressParser.TryParse(entry, out var exact) && exact.Equals(address)) return true;
            if (entry.Contains('/') && IPNetwork.TryParse(entry, out var network) && network.Contains(address)) return true;
        }

        return false;
    }
}
