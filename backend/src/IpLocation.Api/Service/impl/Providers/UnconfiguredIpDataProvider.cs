using System.Net;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service.impl.Providers;

/// <summary>只登记尚未接入的来源，不读取库、不下载文件、不制造查询结果。</summary>
public sealed class UnconfiguredIpDataProvider(ProviderDescriptor descriptor) : IIpDataProvider
{
    public ProviderDescriptor Descriptor { get; } = descriptor;

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderQueryResult(Descriptor.Status, new IpFields(), Descriptor.Message));
}
