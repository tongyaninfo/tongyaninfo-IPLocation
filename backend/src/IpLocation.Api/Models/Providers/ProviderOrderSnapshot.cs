using IpLocation.Api.Service;

namespace IpLocation.Api.Models.Providers;

/// <summary>修订号与只读有序列表一起发布，同一次查询持有同一列表快照。</summary>
public sealed record ProviderOrderSnapshot(long Revision, IReadOnlyList<IIpDataProvider> Providers);
