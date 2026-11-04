using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

/// <summary>只有经过后台白名单和管理员身份检查的控制器可以调用。</summary>
public interface IConsoleReadService
{
    Task<ConsoleOverviewResponse> GetOverviewAsync(ConsoleReadWindowRequest request, CancellationToken cancellationToken);
    Task<ConsoleAccessLogPage> GetAccessLogsAsync(ConsoleAccessLogReadRequest request, CancellationToken cancellationToken);
    Task<ConsoleAuditPage> GetAuditsAsync(ConsoleAuditReadRequest request, CancellationToken cancellationToken);
}
