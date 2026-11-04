using System.Text;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>只使用受控路径和内存队列；验证完整请求目标的采集与凭据边界，不访问真实数据库。</summary>
public sealed class AccessRequestPathTests
{
    [Theory]
    [InlineData("/?ip=2001%3A4860%3A4860%3A%3A8888&db=all&note=a+b&note=a%20b&empty=&flag&")]
    [InlineData("/search/2001%3A4860%3A4860%3A%3A8888/txt?db=all&note=%26%3D%2f")]
    [InlineData("/?IP=1.1.1.1&IP=8.8.8.8&db=dbip")]
    public void NonSensitiveQueryKeepsOriginalOrderingDuplicatesEscapingAndEmptyValues(string path)
    {
        var result = AccessRequestPaths.Normalize(path, out var truncated);
        Assert.Equal(path, result);
        Assert.False(truncated);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("PASSWORD")]
    [InlineData("api%2Dkey")]
    [InlineData("client_secret")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("session")]
    [InlineData("signature")]
    [InlineData("credential")]
    public void SensitiveParameterValuesAreRedactedWithoutChangingOtherQuerySegments(string name)
    {
        var path = $"/?ip=1.1.1.1&{name}=credential-never-store&db=all&{name}=second-secret";
        var result = AccessRequestPaths.Normalize(path, out _);
        Assert.Equal($"/?ip=1.1.1.1&{name}=[REDACTED]&db=all&{name}=[REDACTED]", result);
        Assert.DoesNotContain("credential-never-store", result);
        Assert.DoesNotContain("second-secret", result);
    }

    [Fact]
    public async Task RawRequestTargetIsCapturedBeforeDownstreamRewritesPathOrQuery()
    {
        const string original = "/proxy/search/1.1.1.1?db=all&note=a%2fb&note=a+b&token=private";
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/proxy";
        context.Request.Path = "/search/1.1.1.1";
        context.Request.QueryString = new QueryString("?db=all&note=a%2fb&note=a+b&token=private");
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = original;
        var options = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var middleware = new AccessLogMiddleware(http =>
        {
            http.Request.Path = "/internal/rewritten";
            http.Request.QueryString = new QueryString("?replacement=changed");
            http.Features.Get<IHttpRequestFeature>()!.RawTarget = "/internal/rewritten?replacement=changed";
            return Task.CompletedTask;
        }, options, TimeProvider.System);

        await middleware.InvokeAsync(context, queue);

        Assert.True(queue.TryRead(out var recorded));
        var snapshot = AccessRequestHeaders.Read(recorded.RequestHeadersJson)!;
        Assert.Equal(original.Replace("token=private", "token=[REDACTED]"), snapshot.RequestPath);
        Assert.DoesNotContain("replacement", recorded.RequestHeadersJson);
    }

    [Fact]
    public async Task MissingRawTargetFallsBackToEncodedPathBasePathAndUnchangedQuery()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/网关";
        context.Request.Path = "/search/1.1.1.1";
        context.Request.QueryString = new QueryString("?db=all&note=%E4%B8%AD%E6%96%87&note=a+b");
        var options = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(options, TimeProvider.System);
        await new AccessLogMiddleware(_ => Task.CompletedTask, options, TimeProvider.System).InvokeAsync(context, queue);

        Assert.True(queue.TryRead(out var recorded));
        Assert.Equal("/%E7%BD%91%E5%85%B3/search/1.1.1.1?db=all&note=%E4%B8%AD%E6%96%87&note=a+b",
            AccessRequestHeaders.Read(recorded.RequestHeadersJson)!.RequestPath);
    }

    [Fact]
    public void LongEscapedPathHasIndependentBudgetAndRetainsTruncationAcrossNormalization()
    {
        var headers = new HeaderDictionary { ["User-Agent"] = "controlled-agent" };
        var path = "/?note=" + new string('中', AccessRequestPaths.MaxEncodedBytes);
        var json = AccessRequestHeaders.Capture(headers, path)!;
        var snapshot = AccessRequestHeaders.Read(json)!;
        Assert.NotNull(snapshot.RequestPath);
        Assert.StartsWith("/?note=", snapshot.RequestPath);
        Assert.True(snapshot.RequestPathTruncated);
        Assert.True(JsonEncodedText.Encode(snapshot.RequestPath).EncodedUtf8Bytes.Length <= AccessRequestPaths.MaxEncodedBytes);
        Assert.True(Encoding.UTF8.GetByteCount(json) <= AccessRequestHeaders.MaxSnapshotBytes + AccessRequestPaths.MaxEncodedBytes + 128);
        Assert.Equal("controlled-agent", snapshot.Headers["user-agent"].Single());
        var normalized = AccessRequestHeaders.Read(AccessRequestHeaders.Normalize(json))!;
        Assert.Equal(snapshot.RequestPath, normalized.RequestPath);
        Assert.True(normalized.RequestPathTruncated);
    }

    [Fact]
    public void ReadAndPersistentPreparationSanitizeStoredPathAndKeepStableEventIdentity()
    {
        const string legacyJson = "{\"headers\":{\"user-agent\":[\"controlled\"]},\"truncated\":false,\"requestPath\":\"/?ip=1.1.1.1&token=private&db=all\",\"requestPathTruncated\":false}";
        var item = new AccessEvent(DateTimeOffset.UtcNow, "path-event", "query", "GET", "HTTP/1.1", "json",
            "all", null, 200, 1, false, "203.0.113.1", "curl", legacyJson);
        var prepared = Assert.Single(AccessLogPersistencePlan.Prepare([item], new ControlledIds()));
        Assert.Equal(AccessLogPersistencePlan.GetEventKey(item), prepared.EventKey);
        var snapshot = AccessRequestHeaders.Read(prepared.Event.RequestHeadersJson)!;
        Assert.Equal("/?ip=1.1.1.1&token=[REDACTED]&db=all", snapshot.RequestPath);
        Assert.DoesNotContain("private", prepared.Event.RequestHeadersJson);
        Assert.Equal(prepared.EventKey, AccessLogPersistencePlan.GetEventKey(item with
        {
            RequestHeadersJson = AccessRequestHeaders.Capture(new HeaderDictionary(), "/?ip=8.8.8.8&db=dbip")
        }));
    }

    [Fact]
    public void HistoricalSnapshotsWithoutPathStayUncollected()
    {
        Assert.Null(AccessRequestHeaders.Read(null));
        var old = AccessRequestHeaders.Read("{\"headers\":{},\"truncated\":false}")!;
        Assert.Null(old.RequestPath);
        Assert.False(old.RequestPathTruncated);
        Assert.Null(AccessRequestHeaders.Read(AccessRequestHeaders.Normalize("{\"headers\":{},\"truncated\":false}"))!.RequestPath);
    }

    private sealed class ControlledIds : IIdService
    {
        public long NewId() => 1;
    }
}
