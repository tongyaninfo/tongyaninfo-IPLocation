using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IpLocation.Api.Common;

/// <summary>访问采集仅注册受限内存组件；不隐式创建数据库、文件或凭据。</summary>
public static class AccessLoggingExtensions
{
    public static IServiceCollection AddAccessLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AccessLogOptions>()
            .Bind(configuration.GetSection(AccessLogOptions.SectionName))
            .Validate(options => options.QueueCapacity is > 0 and <= 1_000_000, "访问队列容量必须为 1 到 1000000。")
            .Validate(options => options.BatchSize is > 0 and <= 10_000, "访问日志批次必须为 1 到 10000。")
            .Validate(options => options.FlushIntervalMilliseconds is > 0 and <= 60_000, "访问日志刷新间隔必须为 1 到 60000 毫秒。")
            .Validate(options => options.WriteTimeoutMilliseconds is > 0 and <= 60_000, "访问日志写入超时必须为 1 到 60000 毫秒。")
            .Validate(options => options.ShutdownDrainMilliseconds is > 0 and <= 60_000, "访问日志排空超时必须为 1 到 60000 毫秒。")
            .Validate(options => options.RecentEventCapacity is > 0 and <= 100_000, "访问记录窗口必须为 1 到 100000。")
            .Validate(options => options.RecentEventRetentionDays is null or >= 0 and <= 365, "访问明细保留期必须留空或为 0 到 365 天。")
            .Validate(options => options.MinuteBucketCapacity is > 0 and <= 10_080, "分钟统计窗口必须为 1 到 10080。")
            .ValidateOnStart();
        services.AddOptions<AccessLocationOptions>().Bind(configuration.GetSection("AccessLogging:SourceLocation"))
            .Validate(value => value.BatchTimeoutMilliseconds is > 0 and <= 5_000 && value.LookupTimeoutMilliseconds is > 0 and <= 1_000 &&
                value.LookupTimeoutMilliseconds <= value.BatchTimeoutMilliseconds, "来源定位的单条与批次截止时间无效。")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAccessEventQueue, AccessEventQueue>();
        services.TryAddSingleton<IAccessLogStore, InMemoryAccessLogStore>();
        services.TryAddSingleton<IAccessLogEnricher, AccessLogEnricher>();
        services.AddHostedService<AccessLogWorker>();
        return services;
    }
}
