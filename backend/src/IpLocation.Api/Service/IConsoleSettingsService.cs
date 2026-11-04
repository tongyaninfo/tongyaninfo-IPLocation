using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

public interface IConsoleSettingsService
{
    ConsoleSettingsResponse GetSettings();
    Task<ConsoleOperationResponse<ConsoleSettingsResponse>> SaveAsync(HttpContext context, ConsoleSettingsRequest request);
}
