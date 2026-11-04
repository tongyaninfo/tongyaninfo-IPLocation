using IpLocation.Api.Models.Events;

namespace IpLocation.Api.Service;

public interface IAccessLogEnricher
{
    ValueTask<IReadOnlyList<AccessEvent>> EnrichAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken);
}
