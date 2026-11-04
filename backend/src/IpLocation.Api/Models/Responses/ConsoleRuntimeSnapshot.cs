using IpLocation.Api.Options;

namespace IpLocation.Api.Models.Responses;

/// <summary>已提交的非敏感运行配置；整体交换，查询路径不访问后台数据库。</summary>
public sealed record ConsoleRuntimeSnapshot(long Revision, QueryOptions Query, string[] AllowedNetworks, bool IsReady,
    ConsoleBasicSettings? Basic = null);
