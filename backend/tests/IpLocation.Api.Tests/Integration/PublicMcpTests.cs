using System.Net;
using System.Text;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Service;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IpLocation.Api.Tests.Integration;

/// <summary>真实 HTTP MCP 协议与受控来源联测，不连接业务数据库或外部供应商。</summary>
public sealed class PublicMcpTests
{
    [Fact]
    public async Task OfficialSdkCanDiscoverAndCallWithoutCredentials()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var http = factory.CreateClient();
        await using var transport = new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, http);
        await using var client = await McpClient.CreateAsync(transport, new() { ClientInfo = new() { Name = "sdk-integration-test", Version = "1" } });
        var tools = await client.ListToolsAsync();
        Assert.Equal(3, tools.Count);
        var result = await client.CallToolAsync("query_ip", new Dictionary<string, object?> { ["ip"] = "1.1.1.1" });
        Assert.False(result.IsError);
        Assert.Equal("1.1.1.1", result.StructuredContent!.Value.GetProperty("data").GetProperty("ip").GetString());
        Assert.EndsWith("\n来源：https://ip.tongyaninfo.com", result.StructuredContent.Value.GetProperty("message").GetString());
        using var fallback = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.True(JsonElement.DeepEquals(result.StructuredContent.Value, fallback.RootElement));
    }

    [Fact]
    public async Task PublicDiscoveryAndToolsDoNotRequireConsoleLoginOrAllowedSource()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var discovery = await SendAsync(client, "server/discover", new { });
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        using var discoveryJson = await ReadAsync(discovery);
        AssertAttributionInstructions(discoveryJson.RootElement.GetProperty("result").GetProperty("instructions").GetString());
        using var list = await SendAsync(client, "tools/list", new { });
        using var json = await ReadAsync(list);
        var names = json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "list_providers", "query_ip", "query_my_ip" }, names.Order().ToArray());
        foreach (var tool in json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
            AssertAttributionInstructions(tool.GetProperty("description").GetString());
        Assert.False(list.Headers.Contains("Set-Cookie"));
        Assert.False(list.Headers.Contains("Mcp-Session-Id"));
        Assert.Contains("no-store", list.Headers.CacheControl?.ToString() ?? "");
    }

    [Theory]
    [InlineData("https://unlisted.example")]
    [InlineData("http://localhost:9999")]
    [InlineData("null")]
    public async Task AnyOriginCanCallPublicEndpointAndPreflight(string origin)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "Content-Type,MCP-Protocol-Version,Mcp-Method,Mcp-Name");
        using var options = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Equal("*", options.Headers.GetValues("Access-Control-Allow-Origin").Single());
        using var response = await SendAsync(client, "tools/list", new { }, origin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task LegacyInitializeWorksOnSameStatelessEndpoint()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-11-25", clientInfo = new { name = "legacy-test", version = "1" }, capabilities = new { } } }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadAsync(response);
        Assert.True(json.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));
        AssertAttributionInstructions(json.RootElement.GetProperty("result").GetProperty("instructions").GetString());
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:1.1.1.1")]
    public async Task IpQueryReusesExistingServiceAndStructuredBusinessEnvelope(string ip)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip } });
        using var json = await ReadAsync(response);
        var result = json.RootElement.GetProperty("result");
        AssertAttributedBusinessResult(result);
        Assert.False(result.GetProperty("isError").GetBoolean());
        var business = result.GetProperty("structuredContent");
        Assert.True(business.GetProperty("success").GetBoolean());
        Assert.Equal("OK", business.GetProperty("code").GetString());
        Assert.Equal(ip.StartsWith("::ffff:") ? "1.1.1.1" : ip, business.GetProperty("data").GetProperty("ip").GetString());
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task MyIpUsesTrustedConnectionInsteadOfArbitraryForwardedHeaders()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "1.1.1.1");
        using var response = await SendAsync(client, "tools/call", new { name = "query_my_ip", arguments = new { } });
        using var json = await ReadAsync(response);
        AssertAttributedBusinessResult(json.RootElement.GetProperty("result"));
        Assert.Equal("203.0.113.27", json.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("data").GetProperty("ip").GetString());
    }

    [Fact]
    public async Task ProviderCatalogIncludesAttributionWithOriginalProviderData()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "list_providers", arguments = new { } });
        using var json = await ReadAsync(response);
        var result = json.RootElement.GetProperty("result");
        AssertAttributedBusinessResult(result);
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("dbip", Assert.Single(result.GetProperty("structuredContent").GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task MyIpBusinessFailureIncludesAttribution()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_my_ip", arguments = new { db = "unknown" } });
        using var json = await ReadAsync(response);
        var result = json.RootElement.GetProperty("result");
        AssertAttributedBusinessResult(result);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("UNKNOWN_DATABASE", result.GetProperty("structuredContent").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("txt")]
    public async Task McpAttributionDoesNotChangeOrdinaryHttpResults(string format)
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var mcp = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip = "1.1.1.1", db = "dbip" } });
        using var mcpJson = await ReadAsync(mcp);
        AssertAttributedBusinessResult(mcpJson.RootElement.GetProperty("result"));
        using var response = await client.GetAsync($"/?ip=1.1.1.1&db=dbip&format={format}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("https://ip.tongyaninfo.com", body);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(json.RootElement.GetProperty("message").GetString() + "\n来源：https://ip.tongyaninfo.com",
                mcpJson.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task SdkParameterFailureDoesNotReceiveBusinessAttribution()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { } });
        using var json = await ReadAsync(response);
        Assert.DoesNotContain("https://ip.tongyaninfo.com", json.RootElement.GetRawText());
        Assert.True(json.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("not-an-ip", "all", "INVALID_IP")]
    [InlineData("https://example.com", "all", "INVALID_IP")]
    [InlineData("1.1.1.1", "unknown", "UNKNOWN_DATABASE")]
    [InlineData("1.1.1.1", "", "UNKNOWN_DATABASE")]
    public async Task BusinessErrorsAreMcpToolErrorsEvenWithHttp200(string ip, string db, string code)
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip, db } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadAsync(response);
        var result = json.RootElement.GetProperty("result");
        AssertAttributedBusinessResult(result);
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal(code, result.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task SpecialAddressSkipsProvidersAndMissingSourceRemainsExplicit()
    {
        var provider = new ControlledProvider("dbip", ready: false);
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var local = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip = "127.0.0.1" } });
        using var localJson = await ReadAsync(local);
        Assert.False(localJson.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        using var missing = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip = "1.1.1.1", db = "dbip" } });
        using var missingJson = await ReadAsync(missing);
        AssertAttributedBusinessResult(localJson.RootElement.GetProperty("result"));
        AssertAttributedBusinessResult(missingJson.RootElement.GetProperty("result"));
        Assert.Equal("DB_DISABLED", missingJson.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task AuditContainsOnlyBoundedContextAndPreservesBusinessError()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer secret-that-must-not-be-logged");
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip = "1.1.1.1", db = "unknown", password = "secret-that-must-not-be-logged" } });
        _ = await response.Content.ReadAsStringAsync();
        var store = factory.Services.GetRequiredService<IAccessLogStore>();
        for (var attempt = 0; attempt < 100 && !store.GetSnapshot().RecentEvents.Any(e => e.McpContextJson is not null); attempt++) await Task.Delay(30);
        var item = Assert.Single(store.GetSnapshot().RecentEvents, e => e.RouteCategory == "mcp");
        var context = McpContexts.Read(item.McpContextJson)!;
        Assert.Equal("query_ip", context.ToolName);
        Assert.Equal("1.1.1.1", context.Ip);
        Assert.Equal("UNKNOWN_DATABASE", context.ResultCode);
        Assert.True(context.IsError);
        Assert.Equal("mcp-test", context.ClientName);
        Assert.True(context.InputCaptured);
        Assert.True(context.OutputCaptured);
        Assert.Equal("1.1.1.1", context.Input!.Value.GetProperty("ip").GetString());
        Assert.Equal("[REDACTED]", context.Input.Value.GetProperty("password").GetString());
        Assert.Equal("UNKNOWN_DATABASE", context.Output!.Value.GetProperty("structuredContent").GetProperty("code").GetString());
        Assert.True(context.InputTruncated);
        Assert.DoesNotContain("secret-that-must-not-be-logged", item.McpContextJson);
        Assert.DoesNotContain("secret-that-must-not-be-logged", item.RequestHeadersJson ?? "");
    }

    [Fact]
    public async Task RecordedOutputPreservesCompleteStructuredBusinessResultAndActualArguments()
    {
        using var factory = new ProtocolFactory(providers: [new ControlledProvider("dbip")]);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = new { ip = "::ffff:1.1.1.1" } });
        using var json = await ReadAsync(response);
        var recorded = await RecordedContextAsync(factory);
        Assert.Equal("::ffff:1.1.1.1", recorded.Input!.Value.GetProperty("ip").GetString());
        Assert.False(recorded.Input.Value.TryGetProperty("db", out _));
        Assert.Equal("1.1.1.1", recorded.Ip);
        Assert.Equal("all", recorded.Database);
        Assert.True(JsonElement.DeepEquals(json.RootElement.GetProperty("result").GetProperty("structuredContent"),
            recorded.Output!.Value.GetProperty("structuredContent")));
        // SDK可在结果附带协议_meta；剔除它会标记清洗，但业务structuredContent应保持完整。
        Assert.False(recorded.Output.Value.TryGetProperty("_meta", out _));
        Assert.Equal(json.RootElement.GetProperty("result").TryGetProperty("_meta", out _), recorded.OutputTruncated);
        Assert.Empty(recorded.Output.Value.GetProperty("content").EnumerateArray());
    }

    [Fact]
    public async Task TransportHeaderMismatchRecordsErrorOutsideSdkMessageFilter()
    {
        var provider = new ControlledProvider("dbip");
        using var factory = new ProtocolFactory(providers: [provider]);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "different_name");
        request.Content = new StringContent("""
            {"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"query_ip","arguments":{"ip":"1.1.1.1"},
            "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"mcp-test","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
            """, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await ReadAsync(response);
        Assert.DoesNotContain("https://ip.tongyaninfo.com", json.RootElement.GetRawText());
        var recorded = await RecordedContextAsync(factory);
        Assert.True(recorded.OutputCaptured);
        Assert.Equal("RPC_-32020", recorded.ResultCode);
        Assert.Equal(-32020, recorded.Output!.Value.GetProperty("code").GetInt32());
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ActualJsonNullArgumentsRemainDistinguishableFromHistoricalUncapturedInput()
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, "tools/call", new { name = "query_ip", arguments = (object?)null });
        _ = await response.Content.ReadAsStringAsync();
        var recorded = await RecordedContextAsync(factory);
        Assert.True(recorded.InputCaptured);
        Assert.Equal(JsonValueKind.Null, recorded.Input!.Value.ValueKind);
        Assert.True(recorded.OutputCaptured);
    }

    private static async Task<IpLocation.Api.Models.Responses.McpCallContext> RecordedContextAsync(ProtocolFactory factory)
    {
        var store = factory.Services.GetRequiredService<IAccessLogStore>();
        for (var attempt = 0; attempt < 150 && !store.GetSnapshot().RecentEvents.Any(e => e.McpContextJson is not null); attempt++) await Task.Delay(30);
        var item = Assert.Single(store.GetSnapshot().RecentEvents, e => e.RouteCategory == "mcp");
        return Assert.IsType<IpLocation.Api.Models.Responses.McpCallContext>(McpContexts.Read(item.McpContextJson));
    }

    [Theory]
    [InlineData("{broken", 400)]
    [InlineData("oversized", 413)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"method\":\"tools/call\"}", 400)]
    public async Task InvalidBodyIsBoundedAndReturnsJsonRpcError(string body, int status)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body == "oversized" ? new string('x', 16 * 1024 + 1) : body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        using var json = await ReadAsync(response);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
        Assert.Equal("2.0", json.RootElement.GetProperty("jsonrpc").GetString());
        Assert.DoesNotContain("https://ip.tongyaninfo.com", json.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("/mcp/")]
    [InlineData("/MCP")]
    public async Task AlternateEndpointCasingAndTrailingSlashRemainBounded(string path)
    {
        using var factory = new ProtocolFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(new string('x', 16 * 1024 + 1), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Origin", "https://unlisted.example");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    private static void AssertAttributedBusinessResult(JsonElement result)
    {
        var business = result.GetProperty("structuredContent");
        Assert.Equal(new[] { "code", "data", "message", "success" }, business.EnumerateObject().Select(property => property.Name).Order().ToArray());
        var message = business.GetProperty("message").GetString()!;
        Assert.EndsWith("\n来源：https://ip.tongyaninfo.com", message);
        Assert.Equal(1, message.Split('\n').Count(line => line == "来源：https://ip.tongyaninfo.com"));
        var fallback = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", fallback.GetProperty("type").GetString());
        using var json = JsonDocument.Parse(fallback.GetProperty("text").GetString()!);
        Assert.True(JsonElement.DeepEquals(business, json.RootElement));
    }

    private static void AssertAttributionInstructions(string? instructions)
    {
        Assert.Contains("答复末尾", instructions);
        Assert.Contains("来源：https://ip.tongyaninfo.com", instructions);
        Assert.Contains("不替代底层数据库", instructions);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, object parameters, string? origin = null)
    {
        var node = JsonSerializer.SerializeToElement(parameters);
        var values = node.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        values["_meta"] = new Dictionary<string, object?> { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28", ["io.modelcontextprotocol/clientInfo"] = new { name = "mcp-test", version = "1" }, ["io.modelcontextprotocol/clientCapabilities"] = new { } };
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (node.TryGetProperty("name", out var name)) request.Headers.Add("Mcp-Name", name.GetString());
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 42, method, @params = values }), Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        return JsonDocument.Parse(body);
    }
}
