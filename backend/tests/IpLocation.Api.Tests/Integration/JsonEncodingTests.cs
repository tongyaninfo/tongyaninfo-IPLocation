using System.Net;
using System.Text.Json;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Service;
using IpLocation.Api.Tests.Common;

namespace IpLocation.Api.Tests.Integration;

/// <summary>检查实际 HTTP 正文，不能只解析 JSON 后断言中文，以免漏掉 Unicode 转义回归。</summary>
public sealed class JsonEncodingTests
{
    private const string SourceText = "中国 · 纯真 <script>&'\"\\\r\n\t";

    [Theory]
    [InlineData("CZ88", false)]
    [InlineData("chunzhen", false)]
    [InlineData("CZ88", true)]
    [InlineData("chunzhen", true)]
    public async Task QueryJsonKeepsChineseReadableAndEscapesHtmlAndControls(string code, bool rootQuery)
    {
        using var factory = new ProtocolFactory(providers: [new ChineseProvider()]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(rootQuery ? $"/?ip=1.1.1.1&db={code}" : $"/search/1.1.1.1/json?db={code}");
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var body = await response.Content.ReadAsStringAsync();
        AssertReadableAndEscaped(body);
        using var json = JsonDocument.Parse(body);
        var source = json.RootElement.GetProperty("data").GetProperty("sources")[0];
        Assert.Equal("CZ88", source.GetProperty("id").GetString());
        Assert.Equal(SourceText, source.GetProperty("fields").GetProperty("isp").GetString());
    }

    [Fact]
    public async Task MvcJsonUsesTheSameReadableAndEscapedRepresentation()
    {
        using var factory = new ProtocolFactory(providers: [new ChineseProvider()]);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/providers");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        AssertReadableAndEscaped(body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(SourceText, json.RootElement.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/health", HttpStatusCode.OK, "进程可响应")]
    [InlineData("/api/console/unimplemented", HttpStatusCode.ServiceUnavailable, "管理员持久化暂不可用")]
    public async Task MinimalApiJsonKeepsChineseReadable(string path, HttpStatusCode status, string text)
    {
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(text, body);
        using var json = JsonDocument.Parse(body);
        Assert.Contains(text, json.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UnlistedConsoleResponseContainsNoJsonOrManagementHint()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/console/access");
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Theory]
    [InlineData("/console//invalid")]
    [InlineData("/public//invalid")]
    public async Task GenericAmbiguousPathErrorStillUsesReadableChineseJson(string path)
    {
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("请求路径包含不允许的目录或分隔符。", body);
        Assert.DoesNotContain("后台", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("AMBIGUOUS_PATH", json.RootElement.GetProperty("code").GetString());
    }

    private static void AssertReadableAndEscaped(string body)
    {
        Assert.Contains("中国 · 纯真", body);
        Assert.Contains("\\u003Cscript\\u003E\\u0026\\u0027", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\r\\n\\t", body);
        Assert.DoesNotContain("<script>", body);
        Assert.DoesNotContain('\r', body);
        Assert.DoesNotContain('\n', body);
        Assert.DoesNotContain('\t', body);
    }

    private sealed class ChineseProvider : IIpDataProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("CZ88", SourceText, "ready", true, false,
            true, false, true, "fixture-v1", "受控中文来源", "https://example.com/license", "测试夹具");

        public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderQueryResult("success", new IpFields { Country = "中国", Isp = SourceText }, "受控中文结果", "fixture-v1"));
    }
}
