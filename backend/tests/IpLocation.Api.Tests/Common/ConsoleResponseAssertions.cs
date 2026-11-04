using System.Net;

namespace IpLocation.Api.Tests.Common;

/// <summary>后台 IP 门禁与普通未知地址都不能从正文、登录跳转或身份响应头泄露入口。</summary>
internal static class ConsoleResponseAssertions
{
    public static async Task EmptyNotFoundAsync(HttpResponseMessage response, bool requireNoStore = true)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.False(response.Headers.Contains("Location"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        if (requireNoStore) Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
