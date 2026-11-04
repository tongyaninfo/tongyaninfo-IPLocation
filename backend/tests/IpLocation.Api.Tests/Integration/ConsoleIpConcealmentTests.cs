using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IpLocation.Api.Service;
using IpLocation.Api.Tests.Common;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Integration;

/// <summary>隔离 HTTP 宿主验证 IP 拒绝不暴露后台；不连接数据库或读取真实会话。</summary>
public sealed class ConsoleIpConcealmentTests
{
    public static IEnumerable<object[]> ProtectedRequests()
    {
        foreach (var path in new[] { "/CoNsOlE/login", "/ApI/CoNsOlE/auth/login", "/CoNsOlE-AsSeTs/private.js", "/AsSeTs/CoNsOlE-private.css.br" })
        foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE" })
            yield return [path, method];
    }

    [Theory]
    [MemberData(nameof(ProtectedRequests))]
    public async Task UnlistedMethodsAndMixedCasePathsReturnOnlyEmptyNotFound(string path, string method)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Accept", "text/html, application/json");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        request.Headers.Add("X-Real-IP", "198.51.100.25");
        request.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        request.Headers.Add("Cookie", "Iplocation.Console.Auth=invalid-fixture");
        using var response = await client.SendAsync(request);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("OPTIONS")]
    public async Task UnknownPublicAndProtectedPathsShareEmptyBodyAndNoIdentityHeaders(string method)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var unknownRequest = new HttpRequestMessage(new HttpMethod(method), "/not-a-real-public-entry");
        using var unknown = await client.SendAsync(unknownRequest);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(unknown, requireNoStore: false);

        foreach (var path in new[] { "/console/not-a-real-page", "/api/console/not-a-real-api", "/console-assets/not-a-real-file.css", "/assets/console-never-built.js.gz" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);
            await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
        }
    }

    [Fact]
    public async Task RuntimeRevocationHidesEntriesBeforeReadingAnExistingAuthenticatedSession()
    {
        var identities = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: identities);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        using var tokenJson = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        {
            Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password })
        };
        loginRequest.Headers.Add("X-CSRF-TOKEN", tokenJson.RootElement.GetProperty("data").GetProperty("token").GetString());
        using var login = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var allowed = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // 只发布内存白名单快照；同一客户端保留 Cookie，证明撤销先于身份/防伪与静态文件处理。
        var runtime = factory.Services.GetRequiredService<IConsoleRuntimeSettings>();
        var original = runtime.Current;
        runtime.Publish(original with { Revision = original.Revision + 1, AllowedNetworks = [] });
        var readsBefore = identities.Reads;
        foreach (var path in new[] { "/api/console/access", "/console/login", "/assets/console-private.js" })
        {
            using var response = await client.GetAsync(path);
            await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
        }
        Assert.Equal(readsBefore, identities.Reads);

        runtime.Publish(original with { Revision = original.Revision + 2 });
        using var restored = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }
}
