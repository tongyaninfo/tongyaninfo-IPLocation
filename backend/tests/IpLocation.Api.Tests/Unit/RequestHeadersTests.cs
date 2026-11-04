using System.Text;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace IpLocation.Api.Tests.Unit;

/// <summary>只使用受控请求头与离线 EF 模型，不连接或修改真实数据库。</summary>
public sealed class RequestHeadersTests
{
    [Fact]
    public void CommonHeadersAndMultipleValuesSurviveWithoutKeepingMutableRequestReferences()
    {
        var headers = new HeaderDictionary
        {
            ["User-Agent"] = "ExampleBrowser/1.0",
            ["Referer"] = "https://name:password@example.com/from/path?token=private#fragment",
            ["Origin"] = "https://name:password@example.com?token=private#fragment",
            ["Host"] = "ip.example.com",
            ["Accept"] = new StringValues(["application/json", "text/plain"]),
            ["Accept-Language"] = "zh-CN,en;q=0.8",
            ["Accept-Encoding"] = "gzip, br",
            ["Content-Type"] = "application/json",
            ContentLength = 123
        };
        headers["Sec-CH-UA-Platform"] = "macOS";
        headers["Sec-Fetch-Site"] = "same-origin";
        headers["X-Request-ID"] = "controlled-id";
        headers["traceparent"] = "00-00000000000000000000000000000001-0000000000000001-01";
        var json = AccessRequestHeaders.Capture(headers);
        headers["User-Agent"] = "changed-after-capture";
        var snapshot = Assert.IsType<IpLocation.Api.Models.Responses.RequestHeadersSnapshot>(AccessRequestHeaders.Read(json));
        Assert.Equal("ExampleBrowser/1.0", snapshot.Headers["user-agent"].Single());
        Assert.Equal("https://example.com/from/path", snapshot.Headers["referer"].Single());
        Assert.Equal("https://example.com/", snapshot.Headers["origin"].Single());
        Assert.Equal(new[] { "application/json", "text/plain" }, snapshot.Headers["accept"]);
        Assert.Equal("zh-CN,en;q=0.8", snapshot.Headers["accept-language"].Single());
        Assert.Equal("123", snapshot.Headers["content-length"].Single());
        Assert.Equal("macOS", snapshot.Headers["sec-ch-ua-platform"].Single());
        Assert.Equal("same-origin", snapshot.Headers["sec-fetch-site"].Single());
        Assert.Equal("controlled-id", snapshot.Headers["x-request-id"].Single());
        Assert.False(snapshot.Truncated);
        Assert.DoesNotContain("password", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("fragment", json);
    }

    [Fact]
    public void CredentialsAndArbitraryCustomHeadersAreNeverCaptured()
    {
        var headers = new HeaderDictionary { ["User-Agent"] = "controlled" };
        foreach (var name in new[] { "Cookie", "Authorization", "Proxy-Authorization", "X-API-Key", "X-Token", "Password", "X-CSRF-TOKEN", "X-Custom-Secret", "tracestate" })
            headers[name] = "credential-never-store";
        var json = AccessRequestHeaders.Capture(headers);
        Assert.DoesNotContain("credential-never-store", json);
        Assert.Single(AccessRequestHeaders.Read(json)!.Headers);
        Assert.Equal("controlled", AccessRequestHeaders.Read(json)!.Headers["user-agent"].Single());
    }

    [Fact]
    public void ControlCharactersAndBrokenUnicodeAreRemovedAndLongOrRepeatedValuesAreBounded()
    {
        var headers = new HeaderDictionary { ["User-Agent"] = "safe\r\n\t\0\ud800text" };
        headers["Accept"] = new StringValues(Enumerable.Range(0, 20).Select(_ => new string('x', 3000)).ToArray());
        var snapshot = AccessRequestHeaders.Read(AccessRequestHeaders.Capture(headers))!;
        Assert.Equal("safetext", snapshot.Headers["user-agent"].Single());
        Assert.All(snapshot.Headers.Values.SelectMany(value => value), value => Assert.True(value.Length <= AccessRequestHeaders.MaxValueCharacters));
        Assert.True(snapshot.Headers["accept"].Length <= 8);
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void TotalBudgetCountsActualSerializedUtf8AndJsonEscaping()
    {
        var headers = new HeaderDictionary();
        foreach (var name in new[] { "User-Agent", "Accept", "Accept-Language", "Accept-Encoding", "Cache-Control", "Content-Type", "Sec-CH-UA", "Sec-CH-UA-Platform", "X-Request-ID" })
            headers[name] = new StringValues(Enumerable.Repeat(new string('"', 1600) + "中文😀", 8).ToArray());
        var json = Assert.IsType<string>(AccessRequestHeaders.Capture(headers));
        Assert.True(Encoding.UTF8.GetByteCount(json) <= AccessRequestHeaders.MaxSnapshotBytes);
        using var validJson = JsonDocument.Parse(json);
        var snapshot = AccessRequestHeaders.Read(json)!;
        Assert.True(snapshot.Truncated);
        Assert.True(snapshot.Headers.Count <= AccessRequestHeaders.MaxHeaders);
        Assert.NotEmpty(snapshot.Headers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"headers\":null,\"truncated\":false}")]
    [InlineData("{\"headers\":{\"user-agent\":[1]},\"truncated\":false}")]
    [InlineData("{\"headers\":{},\"truncated\":\"no\"}")]
    public void OldNullAndMalformedSnapshotsReturnNoCollectedMetadata(string? json)
    {
        Assert.Null(AccessRequestHeaders.Read(json));
        Assert.Null(AccessRequestHeaders.Normalize(json));
    }

    [Fact]
    public void EmptyCollectedSnapshotIsDifferentFromOldNullAndStoredValuesAreSanitizedAgain()
    {
        var empty = AccessRequestHeaders.Read(AccessRequestHeaders.Capture(new HeaderDictionary()));
        Assert.NotNull(empty); Assert.Empty(empty.Headers); Assert.False(empty.Truncated);
        var sanitized = AccessRequestHeaders.Normalize("{\"headers\":{\"Authorization\":[\"secret\"],\"Referer\":[\"https://user:password@example.com/path?token=x#y\"]},\"truncated\":false}");
        Assert.DoesNotContain("secret", sanitized);
        Assert.DoesNotContain("password", sanitized);
        Assert.Equal("https://example.com/path", AccessRequestHeaders.Read(sanitized)!.Headers["referer"].Single());
        Assert.True(AccessRequestHeaders.Read(sanitized)!.Truncated);
    }

    [Fact]
    public void UnsafeUrlSchemesAreOmittedAndOpaqueOriginIsPreserved()
    {
        var headers = new HeaderDictionary { ["Referer"] = "javascript:credential", ["Origin"] = "null" };
        var snapshot = AccessRequestHeaders.Read(AccessRequestHeaders.Capture(headers))!;
        Assert.False(snapshot.Headers.ContainsKey("referer"));
        Assert.Equal("null", snapshot.Headers["origin"].Single());
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void AccessLogModelAddsOnlyNullableJsonMetadataProperties()
    {
        using var db = new IpLocationDbContext(new DbContextOptionsBuilder<IpLocationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=offline_headers_model").Options);
        var entity = db.Model.FindEntityType(typeof(AccessLog))!;
        foreach (var name in new[] { nameof(AccessLog.RequestHeadersJson), nameof(AccessLog.SourceLocationJson) })
        {
            var property = entity.FindProperty(name)!;
            Assert.True(property.IsNullable);
            Assert.Equal("jsonb", property.GetColumnType());
        }
    }
}
