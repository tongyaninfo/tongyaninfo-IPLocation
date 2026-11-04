using System.Threading.RateLimiting;

namespace IpLocation.Api.Common;

/// <summary>账号维度限制与入口 IP 限制并行，避免同一账号被多个来源集中尝试。</summary>
public sealed class ConsoleLoginAttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(account =>
        RateLimitPartition.GetFixedWindowLimiter(account, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0
        }));

    public bool TryAcquire(string normalizedAccount)
    {
        using var lease = _limiter.AttemptAcquire(normalizedAccount);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
