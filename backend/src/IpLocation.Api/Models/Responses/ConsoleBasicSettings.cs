namespace IpLocation.Api.Models.Responses;

/// <summary>可保存的非敏感基础设置；队列容量重启生效，会话时长仅用于新登录。</summary>
public sealed record ConsoleBasicSettings(int? AccessLogRetentionDays, bool RetainFullSourceIp,
    int SessionMinutes, int QueueCapacity);
