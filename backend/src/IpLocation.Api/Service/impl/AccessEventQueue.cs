using System.Threading.Channels;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;
using IpLocation.Api.Common;

namespace IpLocation.Api.Service.impl;

/// <summary>使用 TryWrite 明确发现满队列，避免 DropWrite 模式把丢弃误计为接收成功。</summary>
public sealed class AccessEventQueue : IAccessEventQueue
{
    private readonly Channel<AccessEvent> _channel;
    private readonly TimeProvider _timeProvider;
    private long _received;
    private long _accepted;
    private long _read;
    private long _written;
    private long _enqueueDropped;
    private long _consumerDropped;
    private long _suppressed;
    private long _writeFailures;
    private long _lastSuccessTicks;
    private long _lastFailureTicks;
    private int _completed;

    public int Capacity { get; }

    public AccessEventQueue(IOptions<AccessLogOptions> options, TimeProvider timeProvider, IConsoleRuntimeSettings? runtime = null)
    {
        _timeProvider = timeProvider;
        // 启动已加载的持久基础设置决定本次容量；运行中保存新容量不重建或清空队列。
        Capacity = runtime?.Current.Basic?.QueueCapacity ?? options.Value.QueueCapacity;
        _channel = Channel.CreateBounded<AccessEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public void RecordReceived() => Interlocked.Increment(ref _received);

    public bool TryEnqueue(AccessEvent accessEvent)
    {
        if (_channel.Writer.TryWrite(accessEvent))
        {
            Interlocked.Increment(ref _accepted);
            return true;
        }

        Interlocked.Increment(ref _enqueueDropped);
        return false;
    }

    public bool TryRead(out AccessEvent accessEvent)
    {
        if (_channel.Reader.TryRead(out var item))
        {
            accessEvent = item;
            Interlocked.Increment(ref _read);
            return true;
        }

        accessEvent = null!;
        return false;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public void Complete()
    {
        Interlocked.Exchange(ref _completed, 1);
        _channel.Writer.TryComplete();
    }

    public void RecordWritten(int count)
    {
        Interlocked.Add(ref _written, count);
        Interlocked.Exchange(ref _lastSuccessTicks, _timeProvider.GetUtcNow().UtcTicks);
    }

    public void RecordWriteFailure()
    {
        Interlocked.Increment(ref _writeFailures);
        Interlocked.Exchange(ref _lastFailureTicks, _timeProvider.GetUtcNow().UtcTicks);
    }

    public void RecordDroppedAfterRead(int count) => Interlocked.Add(ref _consumerDropped, count);
    public void RecordSuppressedAfterRead(int count) => Interlocked.Add(ref _suppressed, count);

    public AccessQueueMetrics GetMetrics()
    {
        var written = Interlocked.Read(ref _written);
        var consumerDropped = Interlocked.Read(ref _consumerDropped);
        var suppressed = Interlocked.Read(ref _suppressed);
        return new AccessQueueMetrics(
            Interlocked.Read(ref _received),
            Interlocked.Read(ref _accepted),
            written,
            Interlocked.Read(ref _enqueueDropped) + consumerDropped,
            Interlocked.Read(ref _writeFailures),
            _channel.Reader.Count,
            Math.Max(0, Interlocked.Read(ref _read) - written - consumerDropped - suppressed),
            Volatile.Read(ref _completed) == 0,
            ReadTimestamp(ref _lastSuccessTicks),
            ReadTimestamp(ref _lastFailureTicks), suppressed);
    }

    private static DateTimeOffset? ReadTimestamp(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}
