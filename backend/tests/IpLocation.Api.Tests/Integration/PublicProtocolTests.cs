using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Service;
using IpLocation.Api.Tests.Common;

namespace IpLocation.Api.Tests.Integration;

/// <summary>使用隔离 HTTP 宿主验证公开协议，不连接数据库或供应商。</summary>
public sealed class PublicProtocolTests
{
    [Fact]
    public async Task RootReturnsOnlyConnectionIpAndNewline()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("203.0.113.27\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
        Assert.Contains("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task UntrustedForwardingHeaderDoesNotChangeVisitorIp()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "1.1.1.1");
        using var response = await client.SendAsync(request);
        Assert.Equal("203.0.113.27\n", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/search/not-an-ip")]
    [InlineData("/search/1.1.1.1?db=unknown")]
    [InlineData("/search/1.1.1.1?db=")]
    [InlineData("/search/1.1.1.1?db=%20")]
    [InlineData("/search/1.1.1.1?db=dbip&db=ipinfo")]
    [InlineData("/search/1.1.1.1/yaml")]
    public async Task InvalidInputReturnsBusinessError(string url)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task DefaultSourceIsDbIpAndMissingDataIsNotFakeSuccess()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/1.1.1.1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("dbip", root.GetProperty("data").GetProperty("sources")[0].GetProperty("id").GetString());
        using var providers = JsonDocument.Parse(await client.GetStringAsync("/api/providers"));
        var geo = providers.RootElement.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "geolite2");
        Assert.False(geo.GetProperty("enabled").GetBoolean());
    }

    [Theory]
    [InlineData("json", "application/json")]
    [InlineData("xml", "application/xml")]
    [InlineData("txt", "text/plain")]
    public async Task SourceFailurePreservesRequestedRepresentation(string format, string contentType)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/search/1.1.1.1/{format}?db=dbip");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("dbip", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/console")]
    [InlineData("/console/databases")]
    [InlineData("/api/console/access")]
    [InlineData("/api/console/databases")]
    [InlineData("/CONSOLE/settings")]
    [InlineData("/console-assets/private.js")]
    [InlineData("/assets/console-private.js")]
    public async Task UnlistedSourceRejectsEveryConsoleEntry(string url)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.0.2.25")]
    [InlineData("198.51.100.25")]
    public async Task DefaultConsoleAllowlistAllowsConnectionButReportsUnconfiguredStorage(string address)
    {
        using var factory = new ProtocolFactory(connectionAddress: address);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CONSOLE_STORAGE_UNAVAILABLE", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task ForgedLocalHostAndUntrustedForwardingHeadersCannotBypassConsoleAllowlist()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/console/access");
        request.Headers.Host = "localhost";
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        request.Headers.Add("X-Real-IP", "192.0.2.25");
        using var response = await client.SendAsync(request);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Fact]
    public async Task TrustedLocalProxyDoesNotGrantPublicVisitorConsoleAccess()
    {
        using var factory = new ProtocolFactory(knownProxy: "127.0.0.1", connectionAddress: "127.0.0.1");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/console/access");
        request.Headers.Add("X-Forwarded-For", "203.0.113.27");
        using var response = await client.SendAsync(request);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Fact]
    public async Task RegisteredSourcesDoNotPretendToBeLoaded()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/providers");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = json.RootElement.GetProperty("data");
        Assert.Equal(6, providers.GetArrayLength());
        Assert.Contains(providers.EnumerateArray(), source => source.GetProperty("id").GetString() == "ip2location");
    }

    [Theory]
    [InlineData("CZ88")]
    [InlineData("chunzhen")]
    [InlineData("cz88")]
    public async Task EnabledChunzhenWithoutFileRemainsExplicitlyNotReadyOverHttp(string code)
    {
        var provider = new IpLocation.Api.Service.impl.Providers.UnconfiguredIpDataProvider(new ProviderDescriptor("chunzhen", "纯真社区版", "notReady",
            true, true, true, false, false, null, "尚未配置纯真数据文件/读取器。", "https://example.com", "test"));
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var providers = await client.GetAsync("/api/providers");
        using var list = JsonDocument.Parse(await providers.Content.ReadAsStringAsync());
        Assert.True(list.RootElement.GetProperty("data")[0].GetProperty("enabled").GetBoolean());
        Assert.False(list.RootElement.GetProperty("data")[0].GetProperty("ready").GetBoolean());
        Assert.Equal("CZ88", list.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        using var response = await client.GetAsync($"/search/8.8.8.8?db={code}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DB_NOT_READY", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("CZ88", json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("CZ88")]
    [InlineData("chunzhen")]
    [InlineData("all")]
    public async Task LegacySourceDescriptorCanQueryWithoutLosingEligibleMatch(string code)
    {
        var legacy = new ControlledProvider("chunzhen");
        using var factory = new ProtocolFactory(providers: [legacy]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/search/8.8.8.8?db={code}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("success", json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("CZ88", json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("id").GetString());
        Assert.Equal(1, legacy.CallCount);
    }

    [Fact]
    public async Task NativeIpv6IsAcceptedWithoutBrackets()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/2001:4860:4860::8888");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2001:4860:4860::8888", json.RootElement.GetProperty("data").GetProperty("ip").GetString());
    }

    [Fact]
    public async Task ExplicitlyTrustedProxyCanForwardVisitorIp()
    {
        using var factory = new ProtocolFactory(knownProxy: "203.0.113.27");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "1.1.1.1");
        using var response = await client.SendAsync(request);
        Assert.Equal("1.1.1.1\n", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("172.18.0.2")]
    public async Task DockerGatewayWithoutMatchingTrustIgnoresForwardingAndCloudflareHeaders(string? knownProxy)
    {
        using var factory = new ProtocolFactory(knownProxy: knownProxy, connectionAddress: "172.18.0.1");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "1.1.1.1");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("CF-Connecting-IP", "8.8.8.8");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("172.18.0.1\n", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public async Task TrustedDockerGatewayRestoresSameVisitorForPlainAndSelfQuery(string address)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(knownProxy: "172.18.0.1", connectionAddress: "172.18.0.1", providers: [provider]);
        using var client = factory.CreateClient();
        using var plainRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        plainRequest.Headers.Add("X-Forwarded-For", address);
        plainRequest.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        using var plainResponse = await client.SendAsync(plainRequest);
        Assert.Equal(HttpStatusCode.OK, plainResponse.StatusCode);
        Assert.Equal(address + "\n", await plainResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, provider.CallCount);

        using var queryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ip/me");
        queryRequest.Headers.Add("X-Forwarded-For", address);
        queryRequest.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        using var queryResponse = await client.SendAsync(queryRequest);
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Contains("no-store", queryResponse.Headers.CacheControl?.ToString() ?? "");
        using var json = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        Assert.Equal(address, json.RootElement.GetProperty("data").GetProperty("ip").GetString());
        Assert.Equal(1, provider.CallCount);
    }

    [Theory]
    [InlineData("https")]
    [InlineData("http")]
    public async Task TrustedDockerGatewayAllowsBothForwardedSchemesForAllowlistedConsole(string scheme)
    {
        using var factory = new ProtocolFactory(knownProxy: "172.18.0.1", connectionAddress: "172.18.0.1");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/console/access");
        request.Headers.Add("X-Forwarded-For", "192.0.2.25");
        request.Headers.Add("X-Forwarded-Proto", scheme);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CONSOLE_STORAGE_UNAVAILABLE", json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("203.0.113.27")]
    [InlineData("172.18.0.1")]
    public async Task TrustedDockerGatewayConsumesOnlyRightmostForwardedHop(string rightmostAddress)
    {
        using var factory = new ProtocolFactory(knownProxy: "172.18.0.1", connectionAddress: "172.18.0.1");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        // 即使下一跳仍是可信网关，也不能超出默认单跳限制消费左侧伪造值。
        request.Headers.Add("X-Forwarded-For", "127.0.0.1, " + rightmostAddress);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rightmostAddress + "\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TrustedDockerGatewayCannotUseForgedCloudflareHeaderToGrantConsoleAccess()
    {
        using var factory = new ProtocolFactory(knownProxy: "172.18.0.1", connectionAddress: "172.18.0.1");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/console/access");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1, 203.0.113.27");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        using var response = await client.SendAsync(request);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Fact]
    public async Task PlainIpDoesNotInvokeEvenReadyDatabaseProvider()
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task EnabledIp2LocationUsesSameProviderContractWithoutNameSpecificBlock()
    {
        var provider = new ControlledProvider("ip2location");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/1.1.1.1?db=ip2location");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ip2location", json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("id").GetString());
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task AllSkipsBlockedSourcesWithoutFalsePartialStatus()
    {
        var ready = new ControlledProvider("dbip");
        var blocked = new ControlledProvider("ip2location", ready: false);
        using var factory = new ProtocolFactory(providers: [ready, blocked]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/1.1.1.1?db=all");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("success", json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, ready.CallCount);
        Assert.Equal(0, blocked.CallCount);
        Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task AllUnsupportedIsDistinctFromUnavailable()
    {
        var ipv4Only = new ControlledProvider("dbip", supportsIpv6: false);
        using var factory = new ProtocolFactory(providers: [ipv4Only]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/2001:4860:4860::8888?db=all");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unsupported", json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(0, ipv4Only.CallCount);
    }

    [Fact]
    public async Task OneFailedSourceDoesNotEraseSuccessfulResult()
    {
        using var factory = new ProtocolFactory(providers:
            [new ControlledProvider("dbip"), new ControlledProvider("ipinfo", status: "unavailable")]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/1.1.1.1?db=all");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("partial", json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("data").GetProperty("sources")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task PrivateAddressSkipsReadyPublicProvider()
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/search/10.1.2.3");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("private", json.RootElement.GetProperty("data").GetProperty("addressType").GetString());
        Assert.Equal(0, provider.CallCount);
    }
}

/// <summary>测试服务器显式设置连接地址，避免通过请求头伪造生产入口能力。</summary>
internal sealed class ProtocolFactory : WebApplicationFactory<Program>
{
    private readonly string? _knownProxy;
    private readonly IIpDataProvider[]? _providers;
    private readonly string _connectionAddress;
    private readonly IConsoleIdentityStore? _identities;

    public ProtocolFactory(string? knownProxy = null, IIpDataProvider[]? providers = null,
        string connectionAddress = "203.0.113.27", IConsoleIdentityStore? identities = null)
    {
        _knownProxy = knownProxy;
        _providers = providers;
        _connectionAddress = connectionAddress;
        _identities = identities;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>();
            if (_providers is { Length: > 0 }) settings["Query:DefaultDatabase"] = _providers[0].Descriptor.Id;
            if (_knownProxy is not null) settings["TrustedProxies:KnownProxies:0"] = _knownProxy;
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestConnectionFilter(_connectionAddress)));
        if (_identities is not null) builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IConsoleIdentityStore>(_identities)));
        if (_providers is not null) builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIpDataProvider>();
            foreach (var provider in _providers) services.AddSingleton(provider);
        });
    }
}

/// <summary>受控测试来源，仅证明编排行为，不代表接入任何厂商数据。</summary>
internal sealed class ControlledProvider : IIpDataProvider
{
    private int _callCount;
    private readonly string _status;
    public int CallCount => _callCount;
    public ProviderDescriptor Descriptor { get; }

    public ControlledProvider(string id, bool ready = true, bool supportsIpv6 = true, string status = "success")
    {
        _status = status;
        Descriptor = new(id, "受控测试来源", ready ? "ready" : "notReady", true, supportsIpv6,
            ready, ready, ready, ready ? "fixture-v1" : null, "测试夹具", "https://example.com/license", "测试夹具");
    }

    public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(new ProviderQueryResult(_status, new IpFields(), "受控结果", "fixture-v1"));
    }
}

internal sealed class TestConnectionFilter(string connectionAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, continuation) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(connectionAddress);
            return continuation(context);
        });
        next(app);
    };
}
