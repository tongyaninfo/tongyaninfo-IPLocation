using System.Net;

namespace IpLocation.Api.Service;

public interface IConsoleAccessService
{
    bool IsAllowed(IPAddress? address);
}
