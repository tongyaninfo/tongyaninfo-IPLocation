using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IpLocation.Api.Common;

/// <summary>由持久化开关显式启用；不读取连接串，不建库，不执行迁移。</summary>
public static class PostgreSqlAccessLoggingExtensions
{
    public static IServiceCollection AddPostgreSqlAccessLogStore(this IServiceCollection services)
    {
        services.AddOptions<PersistentAccessLogOptions>()
            .BindConfiguration(PersistentAccessLogOptions.SectionName)
            .Validate(options => options.ReadTimeoutMilliseconds is > 0 and <= 60_000, "日志读取超时必须为 1 到 60000 毫秒。")
            .Validate(options => options.MaxRecentRows is > 0 and <= 10_000, "日志读取上限必须为 1 到 10000。")
            .Validate(options => options.MaxMinuteRows is > 0 and <= 10_000, "分钟读取上限必须为 1 到 10000。")
            .Validate(options => options.MaxReadRangeDays is > 0 and <= 366, "日志读取窗口必须为 1 到 366 天。")
            .Validate(options => options.MinuteRetentionDays is > 0 and <= 730, "分钟统计可读保留期必须为 1 到 730 天。")
            .ValidateOnStart();
        services.AddSingleton<PostgreSqlAccessLogStore>();
        services.Replace(ServiceDescriptor.Singleton<IAccessLogStore>(provider => provider.GetRequiredService<PostgreSqlAccessLogStore>()));
        services.AddSingleton<IPersistentAccessLogReader>(provider => provider.GetRequiredService<PostgreSqlAccessLogStore>());
        return services;
    }
}
