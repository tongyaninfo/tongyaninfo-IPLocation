using System.Diagnostics;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Workers;

/// <summary>后台批写普通访问；失败可见且有界，不将日志存储故障传回公共响应。</summary>
public sealed class AccessLogWorker : IHostedService, IDisposable
{
    private readonly IAccessEventQueue _queue;
    private readonly IAccessLogStore _store;
    private readonly AccessLogOptions _options;
    private readonly ILogger<AccessLogWorker> _logger;
    private readonly IAccessLogEnricher? _enricher;
    private readonly AccessLocationOptions _locationOptions;
    private readonly IConsoleRuntimeSettings? _runtime;
    private readonly CancellationTokenSource _forceStop = new();
    private Task? _workerTask;
    private Task<int>? _pendingStoreWrite;
    private Task<IReadOnlyList<AccessEvent>>? _pendingEnrichment;

    public AccessLogWorker(IAccessEventQueue queue, IAccessLogStore store,
        IOptions<AccessLogOptions> options, ILogger<AccessLogWorker> logger,
        IAccessLogEnricher? enricher = null, IOptions<AccessLocationOptions>? locationOptions = null,
        IConsoleRuntimeSettings? runtime = null)
    {
        _queue = queue;
        _store = store;
        _options = options.Value;
        _logger = logger;
        _enricher = enricher;
        _locationOptions = locationOptions?.Value ?? new();
        _runtime = runtime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerTask = ProcessAsync(_forceStop.Token);
        return _workerTask.IsCompleted ? _workerTask : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先关闭写端后限时排空；到期取消消费者，不宣称所有内存事件均已保存。
        _queue.Complete();
        if (_workerTask is null) return;
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(_options.ShutdownDrainMilliseconds);
        try
        {
            await _workerTask.WaitAsync(drain.Token);
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested)
        {
            await _forceStop.CancelAsync();
            _logger.LogWarning("访问日志排空超过限定时间；剩余事件不具备持久化保证。");
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var batch = new List<AccessEvent>(_options.BatchSize);
        try
        {
            while (await _queue.WaitToReadAsync(cancellationToken))
            {
                var deadline = Stopwatch.GetTimestamp();
                var completed = false;
                while (batch.Count < _options.BatchSize)
                {
                    while (batch.Count < _options.BatchSize && _queue.TryRead(out var item)) batch.Add(item);
                    if (batch.Count >= _options.BatchSize) break;
                    var remaining = TimeSpan.FromMilliseconds(_options.FlushIntervalMilliseconds) - Stopwatch.GetElapsedTime(deadline);
                    if (remaining <= TimeSpan.Zero) break;
                    using var flush = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    flush.CancelAfter(remaining);
                    try
                    {
                        if (!await _queue.WaitToReadAsync(flush.Token))
                        {
                            completed = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch.ToArray(), cancellationToken);
                    batch.Clear();
                }
                if (completed) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 限时停止后仅统计未处理数量，不把事件转入无界备用集合。
        }
        finally
        {
            var dropped = batch.Count;
            while (_queue.TryRead(out _)) dropped++;
            if (dropped > 0) _queue.RecordDroppedAfterRead(dropped);
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<AccessEvent> batch, CancellationToken cancellationToken)
    {
        if (!ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _options))
        {
            _queue.RecordSuppressedAfterRead(batch.Count);
            return;
        }
        if (_pendingStoreWrite is { IsCompleted: false })
        {
            // 即使未来存储实现忽略取消，也最多保留一个未结束的调用，不能持续累积后台写入。
            _queue.RecordDroppedAfterRead(batch.Count);
            return;
        }
        if (_pendingStoreWrite?.IsFaulted == true) _ = _pendingStoreWrite.Exception;
        _pendingStoreWrite = null;

        // 定位和存储分开限时；定位失败仍保存原访问事件，不消耗全部数据库写入预算。
        var enriched = await EnrichBatchAsync(batch, cancellationToken);
        if (!ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _options))
        {
            _queue.RecordSuppressedAfterRead(batch.Count);
            return;
        }

        using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        write.CancelAfter(_options.WriteTimeoutMilliseconds);
        try
        {
            _pendingStoreWrite = _store.AppendBatchAsync(enriched, write.Token).AsTask();
            // 停止后也观察潜在的迟到异常；该观察不增加并发写入，不保存额外事件集合。
            _ = _pendingStoreWrite.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var written = await _pendingStoreWrite.WaitAsync(write.Token);
            if (written < 0 || written > batch.Count) throw new InvalidOperationException("访问存储返回的处理数量无效。");
            if (written > 0) _queue.RecordWritten(written);
            if (written < batch.Count) _queue.RecordSuppressedAfterRead(batch.Count - written);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外层 finally 统一计数，避免重复计算当前批次。
            throw;
        }
        catch (Exception)
        {
            // 普通访问尽力记录；不重试结果未知批次，以免未来持久化实现产生重复。
            _queue.RecordWriteFailure();
            _queue.RecordDroppedAfterRead(batch.Count);
            _logger.LogWarning("访问日志批次写入失败，已标记 {Count} 条记录为丢失或结果未知。", batch.Count);
        }
        finally
        {
            if (_pendingStoreWrite?.IsCompleted == true) _pendingStoreWrite = null;
        }
    }

    private async Task<IReadOnlyList<AccessEvent>> EnrichBatchAsync(IReadOnlyList<AccessEvent> batch, CancellationToken cancellationToken)
    {
        if (_enricher is null || !_locationOptions.Enabled) return batch;
        if (_pendingEnrichment is { IsCompleted: false }) return LocationFailed(batch, "timeout");
        if (_pendingEnrichment?.IsFaulted == true) _ = _pendingEnrichment.Exception;
        _pendingEnrichment = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_locationOptions.BatchTimeoutMilliseconds);
        try
        {
            _pendingEnrichment = _enricher.EnrichAsync(batch, deadline.Token).AsTask();
            _ = _pendingEnrichment.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var enriched = await _pendingEnrichment.WaitAsync(deadline.Token);
            return enriched.Count == batch.Count ? enriched : LocationFailed(batch, "unavailable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return LocationFailed(batch, "timeout"); }
        catch (Exception) { return LocationFailed(batch, "unavailable"); }
        finally { if (_pendingEnrichment?.IsCompleted == true) _pendingEnrichment = null; }
    }

    private static IReadOnlyList<AccessEvent> LocationFailed(IReadOnlyList<AccessEvent> batch, string status)
    {
        var json = AccessSourceLocations.Write(AccessLogEnricher.Status(status));
        return batch.Select(item => item.SourceLocationJson is null ? item with { SourceLocationJson = json } : item).ToArray();
    }

    public void Dispose()
    {
        _forceStop.Cancel();
        _forceStop.Dispose();
    }
}
