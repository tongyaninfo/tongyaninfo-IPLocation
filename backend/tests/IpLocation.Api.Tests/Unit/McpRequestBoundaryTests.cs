using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace IpLocation.Api.Tests.Unit;

/// <summary>覆盖协议传输提前结束时的上下文边界，不把无响应正文误标为业务成功。</summary>
public sealed class McpRequestBoundaryTests
{
    [Fact]
    public async Task TransportErrorBeforeSdkMessageFilterStillRecordsActualJsonRpcError()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/mcp";
        http.Request.Method = "GET";
        using var response = new MemoryStream();
        http.Response.Body = response;
        var middleware = new McpRequestMetadataMiddleware(async context =>
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = (string?)null,
                error = new { code = -32020, message = "Header mismatch" } });
        }, NullLogger<McpRequestMetadataMiddleware>.Instance);
        await middleware.InvokeAsync(http);
        var recorded = PublicMcpExtensions.ReadContext(http);
        Assert.Equal("RPC_-32020", recorded.ResultCode);
        Assert.True(recorded.IsError);
        Assert.Equal(-32020, recorded.Output!.Value.GetProperty("code").GetInt32());
        Assert.Equal("Header mismatch", recorded.Output.Value.GetProperty("message").GetString());
        Assert.Same(response, http.Response.Body);
        Assert.True(response.CanWrite);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRequestRemainsCancelledWhetherSdkPropagatesException(bool propagate)
    {
        using var cancellation = new CancellationTokenSource();
        var http = new DefaultHttpContext();
        http.Request.Path = "/mcp";
        http.Request.Method = "GET";
        http.RequestAborted = cancellation.Token;
        var middleware = new McpRequestMetadataMiddleware(_ =>
        {
            cancellation.Cancel();
            if (propagate) throw new OperationCanceledException(cancellation.Token);
            return Task.CompletedTask;
        }, NullLogger<McpRequestMetadataMiddleware>.Instance);
        if (propagate) await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(http));
        else await middleware.InvokeAsync(http);
        var context = PublicMcpExtensions.ReadContext(http);
        Assert.Equal("CANCELLED", context.ResultCode);
        Assert.Equal("cancelled", context.ResultStatus);
        Assert.True(context.IsError);
    }
}
