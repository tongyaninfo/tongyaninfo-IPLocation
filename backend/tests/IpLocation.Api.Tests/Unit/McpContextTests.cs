using System.Text;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;

namespace IpLocation.Api.Tests.Unit;

/// <summary>只验证 MCP 审计的字段边界、兼容、持久化准备和统计，不连接真实数据库。</summary>
public sealed class McpContextTests
{
    [Fact]
    public void ContextRoundTripsOnlyAllowedFieldsAndNormalizesTargetAndProviderAliases()
    {
        var snapshot = McpContexts.Read("""
            {"protocolVersion":"2025-11-25","method":"tools/call","requestId":"42",
             "toolName":"query_ip","clientName":"unverified-client","clientVersion":"1.0",
             "ip":"::ffff:8.8.8.8","database":"chunzhen","resultCode":"OK","resultStatus":"success",
             "isError":false,"sources":[{"id":"chunzhen","version":"2026-11-01","status":"success","rawResponse":"secret-response"}],
             "authorization":"secret-token","_meta":{"prompt":"private-chat"},"body":"secret-body"}
            """);
        Assert.NotNull(snapshot);
        Assert.Equal("8.8.8.8", snapshot.Ip);
        Assert.Equal("CZ88", snapshot.Database);
        Assert.Equal("CZ88", Assert.Single(snapshot.Sources!).Id);
        var json = McpContexts.Write(snapshot)!;
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private-chat", json);
        Assert.DoesNotContain("authorization", json);
        Assert.DoesNotContain("_meta", json);
        Assert.Equal("42", McpContexts.Read(json)!.RequestId);
    }

    [Fact]
    public void InvalidIpUnknownSourcesAndLongValuesAreNotRetainedAsUnboundedMetadata()
    {
        var snapshot = McpContexts.Read(McpContexts.Write(new McpCallContext(
            Method: "tools/call\r\n", RequestId: new string('r', 1024), ClientName: new string('n', 1000),
            Ip: "https://example.com/?secret=one", Database: "unknown", Sources:
            [new("unknown", "v1", "success"), .. Enumerable.Range(0, 10).Select(_ => new McpSourceStatus("dbip", "v1", "success"))])))!;
        Assert.True(snapshot.Truncated);
        Assert.Equal("tools/call", snapshot.Method);
        Assert.Equal(128, snapshot.RequestId!.Length);
        Assert.Equal(128, snapshot.ClientName!.Length);
        Assert.Null(snapshot.Ip);
        Assert.Null(snapshot.Database);
        Assert.InRange(snapshot.Sources!.Count, 0, 6);
        Assert.True(Encoding.UTF8.GetByteCount(McpContexts.Write(snapshot)!) <= McpContexts.MaxSnapshotBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"method\":\"tools/call\",\"Method\":\"initialize\"}")]
    [InlineData("{\"sources\":\"wrong-structure\"}")]
    public void LegacyOrDamagedContextsAreAbsentAndDoNotThrow(string? json) => Assert.Null(McpContexts.Read(json));

    [Fact]
    public void OversizeOrDeepJsonIsRejectedWithoutRetainingBody()
    {
        Assert.Null(McpContexts.Read("{\"clientName\":\"" + new string('a', McpContexts.MaxSnapshotBytes) + "\"}"));
        var depth = McpPayloads.MaxDepth + 5;
        Assert.Null(McpContexts.Read("{\"_meta\":" + new string('[', depth) + "0" + new string(']', depth) + "}"));
    }

    [Fact]
    public void BatchPreparationSanitizesContextAndKeepsExistingEventIdentity()
    {
        var item = Event("first", "query_ip") with
        {
            McpContextJson = "{\"method\":\"tools/call\",\"toolName\":\"query_ip\",\"authorization\":\"secret\"}"
        };
        var prepared = Assert.Single(AccessLogPersistencePlan.Prepare([item], new TestIds()));
        Assert.DoesNotContain("secret", prepared.Event.McpContextJson!);
        Assert.Equal(AccessLogPersistencePlan.GetEventKey(item with { McpContextJson = null }), prepared.EventKey);
        var damaged = Assert.Single(AccessLogPersistencePlan.Prepare([item with { McpContextJson = "{" }], new TestIds()));
        Assert.Null(damaged.Event.McpContextJson);
    }

    [Fact]
    public async Task QueryCountingDistinguishesLocationToolsFromHandshakeAndMetadata()
    {
        var events = new[] { Event("ip", "query_ip"), Event("self", "query_my_ip"), Event("sources", "list_providers"),
            Event("init", null) with { McpContextJson = McpContexts.Write(new McpCallContext(Method: "initialize")) } };
        var prepared = AccessLogPersistencePlan.Prepare(events, new TestIds());
        var minute = Assert.Single(AccessLogPersistencePlan.AggregateInserted(prepared, prepared.Select(item => item.EventKey).ToHashSet()));
        Assert.Equal(4, minute.TotalRequests);
        Assert.Equal(2, minute.QueryRequests);
        var options = Microsoft.Extensions.Options.Options.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var memory = new InMemoryAccessLogStore(options, queue, TimeProvider.System);
        await memory.AppendBatchAsync(events, default);
        var stats = Assert.Single(memory.GetSnapshot().Minutes);
        Assert.Equal(4, stats.TotalRequests);
        Assert.Equal(2, stats.QueryRequests);
        Assert.All(memory.GetSnapshot().RecentEvents, item => Assert.NotNull(item.McpContextJson));
    }

    [Fact]
    public async Task DisabledOrdinaryRetentionAlsoSuppressesMcpAndDoesNotCreateDatabaseWork()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AccessLogOptions { RecentEventRetentionDays = 0 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var memory = new InMemoryAccessLogStore(options, queue, TimeProvider.System);
        Assert.Equal(0, await memory.AppendBatchAsync([Event("disabled", "query_ip")], default));
        Assert.Empty(memory.GetSnapshot().RecentEvents);
    }

    private static AccessEvent Event(string trace, string? tool) => new(DateTimeOffset.UtcNow,
        trace, "mcp", "POST", "HTTP/1.1", "json", "all", null, 200, 1, false, "203.0.113.1", "other",
        McpContextJson: McpContexts.Write(new McpCallContext(Method: "tools/call", ToolName: tool)));

    private sealed class TestIds : IIdService { private long _id; public long NewId() => ++_id; }
}
