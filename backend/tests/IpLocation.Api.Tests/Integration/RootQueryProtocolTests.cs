using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using IpLocation.Api.Common;
using IpLocation.Api.Service;
using IpLocation.Api.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IpLocation.Api.Tests.Integration;

/// <summary>验证根参数协议与旧入口并存，不连接数据库或真实定位文件。</summary>
public sealed class RootQueryProtocolTests
{
    [Theory]
    [InlineData("", "json", "application/json")]
    [InlineData("&format=json", "json", "application/json")]
    [InlineData("&format=xml", "xml", "application/xml")]
    [InlineData("&format=txt", "txt", "text/plain")]
    [InlineData("&FORMAT=TXT", "txt", "text/plain")]
    public async Task ExplicitRootQueryOverridesHtmlAcceptAndKeepsRequestedFormat(string suffix, string format, string contentType)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/?IP=1.1.1.1&DB=all" + suffix);
        request.Headers.Add("Accept", "text/html");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        AssertQueryIp(body, format, "1.1.1.1");
        Assert.Contains("受控测试来源", body);
        Assert.Equal(1, provider.CallCount);
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1")]
    [InlineData("2001:4860:4860::8888", "2001:4860:4860::8888")]
    [InlineData("::ffff:1.1.1.1", "1.1.1.1")]
    public async Task RootQueryNormalizesIpWithExistingParser(string input, string expected)
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/?ip=" + Uri.EscapeDataString(input));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryIp(await response.Content.ReadAsStringAsync(), "json", expected);
    }

    [Theory]
    [InlineData("/?db=dbip", "json", "application/json")]
    [InlineData("/?format=json", "json", "application/json")]
    [InlineData("/?format=txt", "txt", "text/plain")]
    [InlineData("/?db=all&format=xml", "xml", "application/xml")]
    public async Task OmittedIpUsesConnectionAddressAndRejectsForgedHeaders(string url, string format, string contentType)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(connectionAddress: "8.8.8.8", providers: [provider]);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Forwarded-For", "1.1.1.1");
        request.Headers.Add("X-Real-IP", "1.1.1.1");
        request.Headers.Add("CF-Connecting-IP", "1.1.1.1");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        AssertQueryIp(await response.Content.ReadAsStringAsync(), format, "8.8.8.8");
        Assert.Equal(1, provider.CallCount);
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    public async Task OmittedRootIpUsesOnlyConfiguredTrustedProxy(string address)
    {
        using var factory = new ProtocolFactory(knownProxy: "203.0.113.27", providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/?db=all");
        request.Headers.Add("X-Forwarded-For", address);
        request.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertQueryIp(await response.Content.ReadAsStringAsync(), "json", address);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?v=release")]
    public async Task RootWithoutQueryKeysPreservesPlainIpFastPath(string url)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("203.0.113.27\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Accept", response.Headers.Vary);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?v=release")]
    public async Task RootWithoutQueryKeysPreservesHtmlHomepage(string url)
    {
        // 夹具独立保留，不依赖前端是否构建，也不自动删除任何文件。
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults", "root-protocol-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        const string html = "<!doctype html><html lang=\"zh-CN\"><title>受控首页</title><body>查询首页</body></html>";
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), html);
        var provider = new ControlledProvider("dbip");
        using var baseline = new ProtocolFactory(providers: [provider]);
        using var factory = baseline.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Frontend:DistPath"] = directory })));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "text/html");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(html, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("Accept", response.Headers.Vary);
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData("/?ip", "INVALID_IP", "json")]
    [InlineData("/?ip=", "INVALID_IP", "json")]
    [InlineData("/?ip=%20", "INVALID_IP", "json")]
    [InlineData("/?ip=1.1.1.1&IP=8.8.8.8", "INVALID_IP", "json")]
    [InlineData("/?ip=&format=xml", "INVALID_IP", "xml")]
    [InlineData("/?ip=1.1.1.1&ip=8.8.8.8&format=txt", "INVALID_IP", "txt")]
    [InlineData("/?ip=1.1.1.1%2F24&format=xml", "INVALID_IP", "xml")]
    [InlineData("/?ip=example.com&format=txt", "INVALID_IP", "txt")]
    [InlineData("/?ip=%201.1.1.1", "INVALID_IP", "json")]
    [InlineData("/?format", "INVALID_FORMAT", "json")]
    [InlineData("/?ip=1.1.1.1&format=", "INVALID_FORMAT", "json")]
    [InlineData("/?format=%20", "INVALID_FORMAT", "json")]
    [InlineData("/?format=json&FORMAT=txt", "INVALID_FORMAT", "json")]
    [InlineData("/?format=yaml", "INVALID_FORMAT", "json")]
    [InlineData("/?ip=1.1.1.1&db=", "UNKNOWN_DATABASE", "json")]
    [InlineData("/?db=%20&format=xml", "UNKNOWN_DATABASE", "xml")]
    [InlineData("/?ip=1.1.1.1&db=dbip&db=ipinfo&format=txt", "UNKNOWN_DATABASE", "txt")]
    [InlineData("/?db=unknown&format=txt", "UNKNOWN_DATABASE", "txt")]
    public async Task RootParameterErrorsKeepExplicitSemanticsAndDoNotCallProvider(string url, string code, string format)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(connectionAddress: "8.8.8.8", providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        }
        else if (format == "xml")
        {
            Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(code, XDocument.Parse(body).Root!.Element("code")!.Value);
        }
        else
        {
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains($"code={code}\n", body);
        }
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task RootAllKeepsOtherSourceResultsWhenOneSourceFails()
    {
        using var factory = new ProtocolFactory(providers:
            [new ControlledProvider("dbip"), new ControlledProvider("ipinfo", status: "unavailable")]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/?ip=1.1.1.1&db=all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("partial", data.GetProperty("status").GetString());
        Assert.Equal(2, data.GetProperty("sources").GetArrayLength());
        Assert.Equal("success", data.GetProperty("sources")[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/?ip=1.1.1.1&db=all&format=txt", "query", "txt", "all")]
    [InlineData("/?IP=1.1.1.1&DB=all&FORMAT=TXT", "query", "txt", "all")]
    [InlineData("/?db=dbip&format=xml", "selfQuery", "xml", "dbip")]
    [InlineData("/?format=txt", "selfQuery", "txt", "dbip")]
    [InlineData("/?ip=&format=XML", "query", "xml", null)]
    [InlineData("/?format=", "selfQuery", "json", null)]
    [InlineData("/?ip=1.1.1.1&db=dbip&db=ipinfo", "query", "json", null)]
    [InlineData("/", "plainIp", "ip", null)]
    [InlineData("/?v=release", "plainIp", "ip", null)]
    public async Task RootAccessEventsDistinguishPlainSelfAndExplicitQueries(string url, string category, string format, string? database)
    {
        using var baseline = new ProtocolFactory(connectionAddress: "9.9.9.9", providers: [new ControlledProvider("dbip")]);
        using var factory = baseline.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // 只停用测试消费者以直接检查请求侧元数据；真实有界队列与 HTTP 管线保持不变。
            foreach (var descriptor in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                         service.ImplementationType == typeof(AccessLogWorker)).ToArray()) services.Remove(descriptor);
        }));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(url);
        _ = await response.Content.ReadAsStringAsync();
        var queue = factory.Services.GetRequiredService<IAccessEventQueue>();
        Assert.True(queue.TryRead(out var accessEvent));
        Assert.Equal(category, accessEvent.RouteCategory);
        Assert.Equal(format, accessEvent.Format);
        Assert.Equal(database, accessEvent.Database);
        Assert.Equal(url, AccessRequestHeaders.Read(accessEvent.RequestHeadersJson)!.RequestPath);
    }

    private static void AssertQueryIp(string body, string format, string expected)
    {
        if (format == "json")
        {
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(expected, json.RootElement.GetProperty("data").GetProperty("ip").GetString());
        }
        else if (format == "xml") Assert.Equal(expected, XDocument.Parse(body).Root!.Element("data")!.Element("ip")!.Value);
        else Assert.Contains($"ip={expected}\n", body);
    }
}
