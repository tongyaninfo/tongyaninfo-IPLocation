using System.Net;
using IpLocation.Api.Models.Providers;

namespace IpLocation.Api.Service;

public interface IIpDataProvider
{
    ProviderDescriptor Descriptor { get; }
    Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken);
}
