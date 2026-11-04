namespace IpLocation.Api.Common;

/// <summary>只为SDK在消息过滤器之前返回的HTTP错误保留有界副本；直接转发写入，不缓存或延迟正常SSE。</summary>
public sealed class McpResponseCaptureStream(Stream inner, Func<bool> shouldCapture, int limit) : Stream
{
    private readonly MemoryStream _captured = new();
    public bool Truncated { get; private set; }
    public ReadOnlyMemory<byte> Captured => _captured.GetBuffer().AsMemory(0, checked((int)_captured.Length));
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }
    private void Capture(ReadOnlySpan<byte> value)
    {
        if (!shouldCapture()) return;
        var remaining = Math.Max(0, limit - checked((int)_captured.Length));
        if (value.Length > remaining) Truncated = true;
        _captured.Write(value[..Math.Min(value.Length, remaining)]);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _captured.Dispose();
        // 底层流属于ASP.NET请求，不随审计副本一起释放。
        base.Dispose(disposing);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
