using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

public interface IConsoleRuntimeSettings
{
    ConsoleRuntimeSnapshot Current { get; }
    Task ReloadAsync(CancellationToken cancellationToken = default);
    void Publish(ConsoleRuntimeSnapshot snapshot);
}
