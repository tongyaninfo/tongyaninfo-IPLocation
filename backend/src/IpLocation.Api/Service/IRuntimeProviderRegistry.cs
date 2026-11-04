using IpLocation.Api.Models.Providers;

namespace IpLocation.Api.Service;

public interface IRuntimeProviderRegistry
{
    IReadOnlyList<IIpDataProvider> Providers { get; }
    ProviderOrderSnapshot Order { get; }
    long Revision(string id);
    void PublishState(string id, bool enabled, long revision);
    void PublishOrder(IReadOnlyList<string> providerIds, long revision);
    Task ReloadAsync(CancellationToken cancellationToken);
}
