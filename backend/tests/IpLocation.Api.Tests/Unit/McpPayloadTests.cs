using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Tests.Unit;

/// <summary>检查 MCP 入出参容量、有效 JSON 和重复清洗边界，不进行网络或存储操作。</summary>
public sealed class McpPayloadTests
{
    [Fact]
    public void ActualArgumentsAndStructuredBusinessResultRetainNormalFieldsAndNullValues()
    {
        var input = Element(new { ip = "not-an-ip", db = "all" });
        var output = Element(new { isError = false, structuredContent = new
        {
            success = true, code = "OK", message = "查询完成", data = new { ip = "1.1.1.1", status = "success",
                sources = new[] { new { id = "dbip", version = "2026-11", status = "success", fields = new { country = "澳大利亚", city = (string?)null, latitude = -33.8 } } } }
        } });
        var context = McpContexts.Read(McpContexts.Write(new McpCallContext(Method: "tools/call", ToolName: "query_ip", Input: input, Output: output)))!;
        Assert.Equal("not-an-ip", context.Input!.Value.GetProperty("ip").GetString());
        Assert.True(JsonElement.DeepEquals(output, context.Output!.Value));
        Assert.False(context.InputTruncated);
        Assert.False(context.OutputTruncated);
        Assert.False(context.Truncated);
    }

    [Fact]
    public void LegacyContextKeepsNewBodiesAbsentAndJsonNullRemainsACapturedValue()
    {
        var legacy = McpContexts.Read("{\"method\":\"tools/call\",\"toolName\":\"query_ip\",\"resultCode\":\"OK\"}")!;
        Assert.Null(legacy.Input);
        Assert.Null(legacy.Output);
        Assert.False(legacy.InputTruncated);
        Assert.False(legacy.OutputTruncated);
        Assert.False(legacy.InputCaptured);
        Assert.False(legacy.OutputCaptured);
        var captured = McpPayloads.Capture(Element<object?>(null), McpPayloads.MaxInputBytes);
        Assert.True(captured.Value.HasValue);
        Assert.Equal(JsonValueKind.Null, captured.Value.Value.ValueKind);
        Assert.False(captured.Truncated);
        var read = McpContexts.Read(McpContexts.Write(new McpCallContext(Input: captured.Value, Output: captured.Value)))!;
        Assert.True(read.InputCaptured);
        Assert.True(read.OutputCaptured);
        Assert.Equal(JsonValueKind.Null, read.Input!.Value.ValueKind);
        Assert.Equal(JsonValueKind.Null, read.Output!.Value.ValueKind);
    }

    [Fact]
    public void SensitiveKeysAndMetaAreSanitizedAcrossObjectsArraysAndJsonText()
    {
        var input = Element(new
        {
            ip = "1.1.1.1", Password = "sensitive-one", access_token = "sensitive-two",
            nested = new[] { new { db_password = "sensitive-three", clientSecret = "sensitive-four", authorization = "sensitive-five", country = "中国" } },
            _meta = new { privateText = "sensitive-six" },
            text = "{\"credentials\":{\"value\":\"sensitive-seven\"},\"ip\":\"1.1.1.1\"}"
        });
        var captured = McpPayloads.Capture(input, McpPayloads.MaxInputBytes);
        var json = captured.Value!.Value.GetRawText();
        Assert.True(captured.Truncated);
        Assert.DoesNotContain("sensitive-", json);
        Assert.DoesNotContain("_meta", json);
        Assert.Equal(McpPayloads.RedactedValue, captured.Value.Value.GetProperty("Password").GetString());
        Assert.Equal("中国", captured.Value.Value.GetProperty("nested")[0].GetProperty("country").GetString());
        using var nested = JsonDocument.Parse(captured.Value.Value.GetProperty("text").GetString()!);
        Assert.Equal(McpPayloads.RedactedValue, nested.RootElement.GetProperty("credentials").GetString());
        Assert.Equal("1.1.1.1", nested.RootElement.GetProperty("ip").GetString());
    }

    [Fact]
    public void EquivalentMcpFallbackIsOmittedAndOtherJsonFallbackCannotBypassRedaction()
    {
        var business = Element(new { success = true, code = "OK", message = "完成", data = new { country = "中国" } });
        var output = Element(new { isError = false, structuredContent = business, content = new[] { new { type = "text", text = business.GetRawText() } } });
        var captured = McpPayloads.Capture(output, McpPayloads.MaxOutputBytes);
        Assert.True(JsonElement.DeepEquals(business, captured.Value!.Value.GetProperty("structuredContent")));
        Assert.Empty(captured.Value.Value.GetProperty("content").EnumerateArray());
        Assert.False(captured.Truncated);
        var fallback = McpPayloads.Capture(Element(new { content = new[] { new { type = "text", text = "{\"token\":\"sensitive-fallback\",\"code\":\"OK\"}" } } }), McpPayloads.MaxOutputBytes);
        Assert.True(fallback.Truncated);
        Assert.DoesNotContain("sensitive-fallback", fallback.Value!.Value.GetRawText());
        using var parsed = JsonDocument.Parse(fallback.Value.Value.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(McpPayloads.RedactedValue, parsed.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void RpcErrorKeepsActualCodeAndMessageWithoutDiagnosticDataOrStack()
    {
        var error = Element(new { code = -32602, message = "Unknown tool", data = new { value = "private-details" }, details = "private-details", stackTrace = "private-stack" });
        var captured = McpPayloads.Capture(error, McpPayloads.MaxOutputBytes);
        Assert.Equal(-32602, captured.Value!.Value.GetProperty("code").GetInt32());
        Assert.Equal("Unknown tool", captured.Value.Value.GetProperty("message").GetString());
        Assert.Equal(2, captured.Value.Value.EnumerateObject().Count());
        Assert.DoesNotContain("private-", captured.Value.Value.GetRawText());
        Assert.True(captured.Truncated);
    }

    [Theory]
    [InlineData("https://account:private-password@example.com/path", "https://example.com/path")]
    [InlineData("https://example.com/path?token=private-token", "https://example.com/path")]
    [InlineData("https://example.com/path#private-fragment", "https://example.com/path")]
    [InlineData("http://account:private-password@example.com/path?token=private-token#private-fragment", "http://example.com/path")]
    public void AbsoluteHttpUrlsDropCredentialsQueryAndFragmentBeforeRecording(string input, string expected)
    {
        var captured = McpPayloads.Capture(Element(new { ip = input }), McpPayloads.MaxInputBytes);
        Assert.True(captured.Truncated);
        Assert.Equal(expected, captured.Value!.Value.GetProperty("ip").GetString());
        Assert.DoesNotContain("private-", captured.Value.Value.GetRawText());
        Assert.True(Encoding.UTF8.GetByteCount(captured.Value.Value.GetRawText()) <= McpPayloads.MaxInputBytes);
    }

    [Theory]
    [InlineData("https://example.com/path")]
    [InlineData("http://example.com/path")]
    [InlineData("not-an-ip")]
    [InlineData("中国 · 澳大利亚 · 😀")]
    [InlineData("1.1.1.1")]
    public void CleanUrlsAndOrdinaryNonUrlStringsRemainUnchanged(string input)
    {
        var captured = McpPayloads.Capture(Element(new { value = input }), McpPayloads.MaxInputBytes);
        Assert.Equal(input, captured.Value!.Value.GetProperty("value").GetString());
        Assert.False(captured.Truncated);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(8192)]
    [InlineData(32768)]
    public void EscapedUnicodeAndManyValuesStayWithinTheExactJsonBudget(int budget)
    {
        var input = Element(Enumerable.Range(0, 100).ToDictionary(index => "field" + index, index => new string('中', 5000) + "😀\"\\\n" + index));
        var captured = McpPayloads.Capture(input, budget);
        Assert.True(captured.Value.HasValue);
        Assert.True(captured.Truncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(captured.Value.Value.GetRawText()), 1, budget);
        using var valid = JsonDocument.Parse(captured.Value.Value.GetRawText());
        Assert.Equal(JsonValueKind.Object, valid.RootElement.ValueKind);
    }

    [Fact]
    public void DeepContainersAndMalformedJsonTextAreOmittedWithoutCredentialFragments()
    {
        var deep = JsonNode.Parse(new string('[', 40) + "\"deep-sensitive-token\"" + new string(']', 40))!;
        var captured = McpPayloads.Capture(JsonSerializer.SerializeToElement(deep), McpPayloads.MaxInputBytes);
        Assert.True(captured.Truncated);
        Assert.DoesNotContain("deep-sensitive-token", captured.Value!.Value.GetRawText());
        using var valid = JsonDocument.Parse(captured.Value.Value.GetRawText());
        var malformed = McpPayloads.Capture(Element(new { text = "{\"token\":\"sensitive-unclosed" }), McpPayloads.MaxInputBytes);
        Assert.True(malformed.Truncated);
        Assert.DoesNotContain("sensitive-unclosed", malformed.Value!.Value.GetRawText());
    }

    [Fact]
    public void ContainerCountAndTotalNodesAreBounded()
    {
        var many = Element(Enumerable.Range(0, 100).Select(index => index).ToArray());
        var captured = McpPayloads.Capture(many, McpPayloads.MaxInputBytes);
        Assert.Equal(McpPayloads.MaxContainerItems, captured.Value!.Value.GetArrayLength());
        Assert.True(captured.Truncated);
        var nodes = Element(Enumerable.Range(0, 64).Select(_ => Enumerable.Range(0, 64).Select(index => index).ToArray()).ToArray());
        var limited = McpPayloads.Capture(nodes, McpPayloads.MaxOutputBytes);
        Assert.True(limited.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(limited.Value!.Value.GetRawText()) <= McpPayloads.MaxOutputBytes);
    }

    [Fact]
    public void WholeContextRetainsMetadataAndFlagsWhenLargeBodiesAreCleanedTwice()
    {
        var input = Element(new { ip = "1.1.1.1", db = "all", extra = new string('中', 12_000) });
        var output = Element(Enumerable.Range(0, 64).ToDictionary(index => "output" + index, _ => new string('中', 8000)));
        var json = McpContexts.Write(new McpCallContext(Method: "tools/call", RequestId: "large-bodies", ToolName: "query_ip",
            ResultCode: "OK", Input: input, Output: output))!;
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, McpContexts.MaxSnapshotBytes);
        var read = McpContexts.Read(json)!;
        Assert.Equal("large-bodies", read.RequestId);
        Assert.Equal("OK", read.ResultCode);
        Assert.True(read.InputTruncated);
        Assert.True(read.OutputTruncated);
        Assert.True(read.Truncated);
        Assert.NotNull(read.Input);
        Assert.NotNull(read.Output);
        var again = McpContexts.Read(McpContexts.Write(read))!;
        Assert.True(again.InputTruncated);
        Assert.True(again.OutputTruncated);
        Assert.True(JsonElement.DeepEquals(read.Input.Value, again.Input!.Value));
        Assert.True(JsonElement.DeepEquals(read.Output.Value, again.Output!.Value));
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
}
