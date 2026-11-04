using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

public interface IConsoleIdentityService
{
    Task<ConsoleOperationResponse<ConsoleAccessResponse>> GetAccessAsync(HttpContext context);
    Task<ConsoleOperationResponse<ConsoleAccessResponse>> LoginAsync(HttpContext context, ConsoleLoginRequest request);
    Task<ConsoleOperationResponse<object>> LogoutAsync(HttpContext context);
}
