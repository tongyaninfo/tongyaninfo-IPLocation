using IpLocation.Api.Common;

namespace IpLocation.Api.Models.Responses;

public sealed record DatabaseManagementResponse(int StatusCode, ApiResult<object> Result);
