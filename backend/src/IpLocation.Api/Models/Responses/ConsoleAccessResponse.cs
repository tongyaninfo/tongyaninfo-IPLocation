using IpLocation.Api.Common;

namespace IpLocation.Api.Models.Responses;

public sealed record ConsoleUserResponse(string Id, string Account, string DisplayName);
public sealed record ConsoleAccessResponse(bool Allowed, bool Initialized, bool Authenticated, string Message,
    ConsoleUserResponse? User);
public sealed record ConsoleOperationResponse<T>(int StatusCode, ApiResult<T> Result);
