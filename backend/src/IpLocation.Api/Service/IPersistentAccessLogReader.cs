using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Service;

/// <summary>只能接入已鉴权的后台；公开 IP 响应不调用此接口。</summary>
public interface IPersistentAccessLogReader
{
    Task<PersistentAccessLogSnapshot> GetSnapshotAsync(AccessLogReadRequest request, CancellationToken cancellationToken);
}
