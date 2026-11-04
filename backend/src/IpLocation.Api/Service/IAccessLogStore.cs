using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

/// <summary>消费者写入与受保护后台读取的边界，不由公开请求直接调用。</summary>
public interface IAccessLogStore
{
    /// <summary>返回已确认处理数量；策略关闭时返回0，不将跳过批次记为写入成功。不能修改传入集合。</summary>
    ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken);
    AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60);
}
