using System.Collections.Concurrent;
using IpLocation.Api.Options;
using IpLocation.Api.Service.impl;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class IdServiceTests
{
    [Fact]
    public void LocalYitterIdsRemainUniqueAcrossConcurrentServiceConsumers()
    {
        // 同进程多个宿主也应复用同一 Worker 的序列，不重置全局静态助手。
        var options = Microsoft.Extensions.Options.Options.Create(new IdGenerationOptions { WorkerId = 61 });
        var first = new YiteIdService(options);
        var second = new YiteIdService(options);
        var ids = new ConcurrentDictionary<long, byte>();
        Parallel.For(0, 10_000, index => Assert.True(ids.TryAdd((index % 2 == 0 ? first : second).NewId(), 0)));
        Assert.Equal(10_000, ids.Count);
        Assert.All(ids.Keys, id => Assert.True(id > 0));
    }

    [Fact]
    public void InvalidWorkerDoesNotSilentlyGenerateIds() => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new YiteIdService(Microsoft.Extensions.Options.Options.Create(new IdGenerationOptions { WorkerId = 64 })));
}
