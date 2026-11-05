using Tongyan.Support.Client.Service;
using Tongyan.Telemetry.Service;

namespace IpLocation.Api.Workers;

/// <summary>支持心跳独立运行，不等待官网可用，不采集任何产品业务数据。</summary>
public sealed class SupportHeartbeatWorker(ProductSupportClient support, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等宿主真正就绪后再宣告在线；异步等待不阻塞Host.StartAsync，启动失败或停止时可取消。
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult(true));
        try { await started.Task.WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        await new HeartbeatRunner(support).RunAsync(stoppingToken);
    }
}

