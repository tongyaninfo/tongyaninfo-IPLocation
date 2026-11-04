using System.Collections.Concurrent;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;
using Yitter.IdGenerator;

namespace IpLocation.Api.Service.impl;

/// <summary>本地发号；同一进程中相同 WorkerId 共用生成器，避免多宿主重复初始化静态助手。</summary>
public sealed class YiteIdService : IIdService
{
    private static readonly ConcurrentDictionary<ushort, Lazy<DefaultIdGenerator>> Generators = new();
    private readonly DefaultIdGenerator _generator;

    public YiteIdService(IOptions<IdGenerationOptions> options)
    {
        var workerId = options.Value.WorkerId;
        if (workerId > 63) throw new ArgumentOutOfRangeException(nameof(options), "默认位宽下 WorkerId 必须在 0 到 63 之间。");
        _generator = Generators.GetOrAdd(workerId, value => new Lazy<DefaultIdGenerator>(
            () => new DefaultIdGenerator(new IdGeneratorOptions(value)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public long NewId() => _generator.NewLong();
}
