using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Service;
using IpLocation.Api.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IpLocation.Api.Tests.Integration;

/// <summary>验证真实路由、白名单、Cookie身份及读取合同；使用受控数据，不连接PG。</summary>
public sealed class ConsoleReadApiTests
{
    [Theory]
    [InlineData("/api/console/overview")]
    [InlineData("/api/console/access-logs")]
    [InlineData("/api/console/mcp-logs?toolName=query_ip&resultCode=OK&traceId=fixture-trace&routeCategory=plainIp")]
    [InlineData("/api/console/audits")]
    public async Task ReadRoutesRequireIdentityAndReturnUnifiedNoStoreResponse(string path)
    {
        var reads = new FixtureReads();
        using var factory = Factory(reads, "127.0.0.1");
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, reads.Calls);
        using var tokenResponse = await client.GetAsync("/api/console/auth/csrf");
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        {
            Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password })
        };
        loginRequest.Headers.Add("X-CSRF-TOKEN", tokenJson.RootElement.GetProperty("data").GetProperty("token").GetString());
        using var login = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("OK", json.RootElement.GetProperty("code").GetString());
        if (path.EndsWith("access-logs", StringComparison.Ordinal))
        {
            Assert.Equal("9007199254740993", json.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetString());
            var item = json.RootElement.GetProperty("data").GetProperty("items")[0];
            Assert.Equal("/?ip=1.1.1.1&db=all&note=a%2Fb", item.GetProperty("requestPath").GetString());
            Assert.False(item.GetProperty("requestPathTruncated").GetBoolean());
        }
        if (path.Contains("mcp-logs", StringComparison.Ordinal))
        {
            Assert.Equal("mcp", reads.LastAccessRequest!.RouteCategory);
            Assert.Equal("query_ip", reads.LastAccessRequest.ToolName);
            Assert.Equal("OK", reads.LastAccessRequest.ResultCode);
            Assert.Equal("fixture-trace", reads.LastAccessRequest.TraceId);
        }
        if (path.EndsWith("overview", StringComparison.Ordinal))
        {
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(7, data.GetProperty("mcpTotals").GetProperty("requests").GetInt64());
            Assert.Equal(3, data.GetProperty("mcpTotals").GetProperty("queryRequests").GetInt64());
            Assert.Equal(1, data.GetProperty("mcpTotals").GetProperty("httpErrors").GetInt64());
            Assert.Equal(7, data.GetProperty("mcpTrend")[0].GetProperty("requests").GetInt64());
        }
        Assert.Equal(1, reads.Calls);
    }

    [Fact]
    public async Task UntrustedSourceIsRejectedBeforeAnyManagementRead()
    {
        var reads = new FixtureReads();
        using var factory = Factory(reads, "203.0.113.7");
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/console/overview", "/api/console/access-logs", "/api/console/mcp-logs", "/api/console/audits" })
        {
            using var response = await client.GetAsync(path);
            await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
        }
        Assert.Equal(0, reads.Calls);
    }

    private static WebApplicationFactory<Program> Factory(FixtureReads reads, string address) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:Enabled"] = "false" }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestConnectionFilter(address)));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IConsoleIdentityStore>(
                    new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() }));
                services.Replace(ServiceDescriptor.Singleton<IConsoleReadService>(reads));
            });
        });

    private sealed class FixtureReads : IConsoleReadService
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly AccessQueueMetrics Queue = new(0, 0, 0, 0, 0, 0, 0, true, null, null);
        public int Calls { get; private set; }
        public ConsoleAccessLogReadRequest? LastAccessRequest { get; private set; }
        public Task<ConsoleOverviewResponse> GetOverviewAsync(ConsoleReadWindowRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ConsoleOverviewResponse(Now.AddDays(-1), Now, Now, "controlled", new(7, 0, 0, 3, 1, 0, 0, null, null),
                [], Queue, Now, 180, ["受控测试数据"], [], new(7, 3, 1), [new(Now.AddMinutes(-1), 7, 3, 1)]));
        }
        public Task<ConsoleAccessLogPage> GetAccessLogsAsync(ConsoleAccessLogReadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAccessRequest = request;
            return Task.FromResult(new ConsoleAccessLogPage([new("9007199254740993", Now, "203.0.113.1", "plainIp", "GET", "ip",
                null, null, 200, 1, false, "curl", "fixture-trace",
                RequestHeaders: new(new Dictionary<string, string[]>(), false, RequestPath: "/?ip=1.1.1.1&db=all&note=a%2Fb"))],
                1, request.Page, request.PageSize, Now.AddDays(-1), Now, 30, Queue));
        }
        public Task<ConsoleAuditPage> GetAuditsAsync(ConsoleAuditReadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ConsoleAuditPage([], 0, request.Page, request.PageSize));
        }
    }
}
