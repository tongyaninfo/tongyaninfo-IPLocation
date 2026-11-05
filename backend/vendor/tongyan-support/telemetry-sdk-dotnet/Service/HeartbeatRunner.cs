using Tongyan.Support.Client.Service;

namespace Tongyan.Telemetry.Service;

/// <summary>宿主在后台调用；启动立即发送，固定五分钟一轮，退出不补发历史数据。</summary>
public sealed class HeartbeatRunner(ProductSupportClient support)
{
    private readonly Guid sessionId = Guid.NewGuid();
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(300));
        while (!ct.IsCancellationRequested)
        {
            await support.HeartbeatAsync(sessionId, ct);
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
