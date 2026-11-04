using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;

namespace IpLocation.Api.Workers;

/// <summary>启动恢复和跨进程刷新不进入公共请求路径；恢复失败时来源保持未就绪。</summary>
public sealed class RuntimeProviderRefreshWorker(IRuntimeProviderRegistry registry, IConsoleRuntimeSettings settings,
    DatabaseManagementService databases, ILogger<RuntimeProviderRefreshWorker> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await settings.ReloadAsync(cancellationToken); await registry.ReloadAsync(cancellationToken);
            await databases.RecoverInterruptedUploadsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("来源持久化状态刷新失败，保留已知状态或未就绪状态；稍后重试。"); }
    }
}
