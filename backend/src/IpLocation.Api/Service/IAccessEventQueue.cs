using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

/// <summary>请求侧只调用 RecordReceived 和 TryEnqueue，永不等待消费者。</summary>
public interface IAccessEventQueue
{
    int Capacity { get; }
    void RecordReceived();
    bool TryEnqueue(AccessEvent accessEvent);
    bool TryRead(out AccessEvent accessEvent);
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken);
    void Complete();
    void RecordWritten(int count);
    void RecordWriteFailure();
    void RecordDroppedAfterRead(int count);
    void RecordSuppressedAfterRead(int count);
    AccessQueueMetrics GetMetrics();
}
