using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Unit;

/// <summary>覆盖真实的协商和静态路径歧义，避免只用正常浏览器请求掩盖入口边界。</summary>
public sealed class NegotiationAndConsolePathTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("*/*", false)]
    [InlineData("text/*", false)]
    [InlineData("application/xml", false)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("TeXt/HtMl, text/plain", true)]
    [InlineData("text/plain, TEXT/HTML", false)]
    [InlineData("text/html,*/*;q=0.8", true)]
    [InlineData("text/html;q=0.2,text/plain;q=0.8", false)]
    [InlineData("text/html;q=0.2,*/*;q=0.8", false)]
    [InlineData("text/html;q=0,*/*;q=1", false)]
    [InlineData("text/plain;q=0,text/html;q=0.8,*/*;q=1", true)]
    [InlineData("application/xml;q=1,text/html;q=0.8,text/plain;q=0.1", true)]
    public void AcceptChoosesOnlySupportedRepresentations(string accept, bool expectedHtml)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = accept;
        Assert.Equal(expectedHtml, FrontendHost.PrefersHtml(context.Request));
    }

    [Theory]
    [InlineData("/assets/x/../console-private.js", "/assets/x/../console-private.js")]
    [InlineData("/assets/x/../console-private.js", "/assets/x/%2e%2e/console-private.js")]
    [InlineData("/assets/x/./console-private.js", "/assets/x/%2e/console-private.js")]
    [InlineData("/assets%2Fconsole-private.js", "/assets%2Fconsole-private.js")]
    [InlineData("/assets%5cconsole-private.js", "/assets%5cconsole-private.js")]
    [InlineData("/assets\\console-private.js", "/assets\\console-private.js")]
    [InlineData("/assets//console-private.js", "/assets//console-private.js")]
    [InlineData("/assets/x/%2fconsole-private.js", "/assets/x/%252fconsole-private.js")]
    public async Task AmbiguousPathsNeverReachStaticFilesEvenForAllowedAddresses(string path, string rawTarget)
    {
        var continued = false;
        var middleware = new ConsoleAccessMiddleware(_ => { continued = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.RequestServices = services;

        await middleware.InvokeAsync(context, new FixedClientIp(), new FixedAccess(true));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/CONSOLE/settings")]
    [InlineData("/API/CONSOLE/access")]
    [InlineData("/CONSOLE-ASSETS/private.js")]
    [InlineData("/ASSETS/CONSOLE-private.js")]
    public async Task ConsolePathsUseTheSameCaseInsensitiveGateAsRouting(string path)
    {
        var continued = false;
        var middleware = new ConsoleAccessMiddleware(_ => { continued = true; return Task.CompletedTask; });
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        context.RequestServices = services;

        await middleware.InvokeAsync(context, new FixedClientIp(), new FixedAccess(false));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Null(context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    private sealed class FixedClientIp : IClientIpService
    {
        public IPAddress? GetClientIp(HttpContext context) => IPAddress.Loopback;
    }

    private sealed class FixedAccess(bool allowed) : IConsoleAccessService
    {
        public bool IsAllowed(IPAddress? address) => allowed;
    }
}
