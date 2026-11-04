using IpLocation.Api.Common;

namespace IpLocation.Api.Models.Responses;

/// <summary>服务返回 HTTP 语义，格式输出层不重新推断业务状态。</summary>
public sealed record QueryResponse(int StatusCode, ApiResult<QueryResult> Result);
