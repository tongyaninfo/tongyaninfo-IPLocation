using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;

namespace IpLocation.Api.Common;

public static class ConsoleAccessExtensions
{
    public static IServiceCollection AddConsoleAccess(this IServiceCollection services, IConfigurationRoot configuration)
    {
        services.AddOptions<ConsoleOptions>().Bind(configuration.GetSection("Console"))
            .PostConfigure(options =>
            {
                // 配置数组默认会逐项合并。白名单必须整份替换，避免收紧名单或显式 [] 后仍放行旧地址。
                const string key = "Console:AllowedNetworks";
                options.AllowedNetworks = [];
                foreach (var provider in configuration.Providers.Reverse())
                {
                    var children = provider.GetChildKeys([], key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!provider.TryGet(key, out _) && children.Length == 0) continue;
                    if (children.Any(child => !int.TryParse(child, out var index) || index < 0)) break;
                    options.AllowedNetworks = children.Select(child =>
                            provider.TryGet($"{key}:{child}", out var value) ? value : null)
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
                    break;
                }
            });
        services.AddSingleton<IConsoleAccessService, ConsoleAccessService>();
        return services;
    }
}
