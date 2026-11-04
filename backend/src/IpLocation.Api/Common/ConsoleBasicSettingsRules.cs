using System.Text.Json;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;

namespace IpLocation.Api.Common;

/// <summary>基础设置复用既有设置表和运行快照；null 是明确的无限期读取值，不是缺省配置。</summary>
public static class ConsoleBasicSettingsRules
{
    public static ConsoleBasicSettings FromOptions(AccessLogOptions logging, ConsoleAuthOptions auth) =>
        new(logging.RecentEventRetentionDays, logging.RetainFullSourceIp, auth.SessionMinutes, logging.QueueCapacity);

    public static bool IsValid(ConsoleBasicSettings basic) =>
        (basic.AccessLogRetentionDays is null or >= 0 and <= 365) &&
        basic.SessionMinutes is >= 5 and <= 1440 && basic.QueueCapacity is >= 1 and <= 1_000_000;

    public static ConsoleBasicSettings Read(IReadOnlyDictionary<string, string> values, ConsoleBasicSettings fallback)
    {
        // 持久键存在且内容为 JSON null 时，Deserialize<int?> 的 null 必须保留。
        T Read<T>(string key, T initial) => values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json)! : initial;
        var basic = new ConsoleBasicSettings(Read("basic.accessLogRetentionDays", fallback.AccessLogRetentionDays),
            Read("basic.retainFullSourceIp", fallback.RetainFullSourceIp),
            Read("basic.sessionMinutes", fallback.SessionMinutes), Read("basic.queueCapacity", fallback.QueueCapacity));
        if (!IsValid(basic)) throw new InvalidOperationException("已保存的基础设置超出允许范围。");
        return basic;
    }

    public static int? RetentionDays(IConsoleRuntimeSettings? runtime, AccessLogOptions fallback) =>
        runtime?.Current.Basic is { } basic ? basic.AccessLogRetentionDays : fallback.RecentEventRetentionDays;

    public static bool IsAccessLoggingEnabled(IConsoleRuntimeSettings? runtime, AccessLogOptions fallback)
    {
        if (!fallback.Enabled) return false;
        if (runtime is null) return fallback.RecentEventRetentionDays != 0;
        // 持久设置尚未可靠加载时，不能拿启动默认值覆盖未知的“停止保存”策略。
        // 同一次判断只读一个完整快照，避免就绪状态和保留策略来自不同修订。
        var snapshot = runtime.Current;
        return snapshot.IsReady &&
            (snapshot.Basic is { } basic ? basic.AccessLogRetentionDays : fallback.RecentEventRetentionDays) != 0;
    }

    /// <summary>0 仅关闭新保存，不删除或隐藏历史；0 和 null 均不设置历史读取截止。</summary>
    public static DateTimeOffset? ReadCutoff(DateTimeOffset now, int? retentionDays) =>
        retentionDays is > 0 ? now.AddDays(-retentionDays.Value) : null;
}
