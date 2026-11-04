namespace IpLocation.Api.Common;

/// <summary>业务响应统一信封；HTTP 状态码同时表达请求是否可处理。</summary>
public sealed record ApiResult<T>(bool Success, string Code, string Message, T? Data)
{
    public static ApiResult<T> Ok(T data, string message = "查询完成") => new(true, "OK", message, data);

    public static ApiResult<T> Fail(string code, string message, T? data = default) => new(false, code, message, data);
}
