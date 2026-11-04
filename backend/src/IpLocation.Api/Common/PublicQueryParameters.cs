namespace IpLocation.Api.Common;

/// <summary>公开查询参数的存在性与单值规则供路由和访问统计共用，避免空值被当成省略。</summary>
public static class PublicQueryParameters
{
    public static bool IsRootQuery(HttpRequest request) => request.Path == "/" &&
        (request.Query.ContainsKey("ip") || request.Query.ContainsKey("db") || request.Query.ContainsKey("format"));

    public static bool HasInvalidSingleValue(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) && (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]));
}
