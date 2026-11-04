using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

public interface IIpQueryService
{
    IReadOnlyList<ProviderDescriptor> GetProviders();
    Task<QueryResponse> QueryAsync(string ip, string? database, CancellationToken cancellationToken);
}
