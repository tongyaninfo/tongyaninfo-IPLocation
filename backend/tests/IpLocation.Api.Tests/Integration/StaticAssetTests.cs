using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IpLocation.Api.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Integration;

/// <summary>使用独立静态目录验证真实协商、缓存与后台门禁；夹具保留，不自动删除文件。</summary>
public sealed class StaticAssetTests
{
    private const string AssetPath = "/assets/app-abc12345.js";

    [Theory]
    [InlineData(AssetPath)]
    [InlineData("/assets/site-def67890.css")]
    [InlineData("/logos/provider.svg")]
    public async Task BrotliAssetsKeepOriginalMimeAndDecompressToOriginalBytes(string path)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, path, "br, gzip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.True(new FileExtensionContentTypeProvider().TryGetContentType(path, out var originalMime));
        Assert.Equal(originalMime, response.Content.Headers.ContentType?.MediaType);
        AssertVaryEncoding(response);
        Assert.Equal(fixture.Read(path), await ReadDecoded(response));
    }

    [Theory]
    [InlineData("br, gzip", "br")]
    [InlineData("gzip;q=1, br;q=0.5", "gzip")]
    [InlineData("gzip;q=0.5, br;q=1", "br")]
    [InlineData("br;q=0, gzip;q=1", "gzip")]
    [InlineData("br;q=1, gzip;q=0", "br")]
    [InlineData("br;q=0, gzip;q=0", null)]
    [InlineData("deflate", null)]
    [InlineData("identity;q=1, br;q=0.8, gzip;q=0.9", null)]
    [InlineData("identity;q=0.5, br;q=0.8, gzip;q=0.9", "gzip")]
    [InlineData("*;q=0, identity;q=1", null)]
    [InlineData("br;q=0.3", "br")]
    [InlineData(null, null)]
    public async Task EncodingQualityAndIdentityAreRespected(string? acceptEncoding, string? encoding)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, AssetPath, acceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (encoding is null) Assert.Empty(response.Content.Headers.ContentEncoding);
        else Assert.Equal(encoding, Assert.Single(response.Content.Headers.ContentEncoding));
        AssertVaryEncoding(response);
        Assert.Equal(fixture.Read(AssetPath), await ReadDecoded(response));
    }

    [Theory]
    [InlineData(AssetPath, "br;q=0, gzip;q=0, identity;q=0")]
    [InlineData(AssetPath, "*;q=0")]
    [InlineData("/assets/gzip-only-abc12345.js", "br, identity;q=0")]
    public async Task ForbiddenIdentityAndUnavailableEncodingsReturn406(string path, string acceptEncoding)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, path, acceptEncoding);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task MissingBrotliSidecarFallsBackToAvailableGzip()
    {
        var fixture = new StaticFixture();
        const string path = "/assets/gzip-only-abc12345.js";
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, path, "br, gzip");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(fixture.Read(path), await ReadDecoded(response));
    }

    [Fact]
    public async Task RangeUsesUncompressedRepresentationAndFramework206()
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AssetPath);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        request.Headers.Range = new RangeHeaderValue(0, 31);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(0, response.Content.Headers.ContentRange?.From);
        Assert.Equal(31, response.Content.Headers.ContentRange?.To);
        Assert.Equal(fixture.Read(AssetPath).Length, response.Content.Headers.ContentRange?.Length);
        AssertVaryEncoding(response);
        Assert.Equal(fixture.Read(AssetPath)[..32], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RangeDoesNotOverrideExplicitIdentityRefusal()
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, AssetPath);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, identity;q=0");
        request.Headers.Range = new RangeHeaderValue(0, 31);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData(null)]
    public async Task ConditionalGetKeepsVaryAndImmutableCacheOn304(string? acceptEncoding)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var initial = await Send(client, AssetPath, acceptEncoding);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.NotNull(initial.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, AssetPath);
        if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        request.Headers.IfNoneMatch.Add(initial.Headers.ETag!);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        AssertVaryEncoding(response);
        AssertImmutable(response);
    }

    [Theory]
    [InlineData(null, "br")]
    [InlineData("br", null)]
    public async Task EtagFromAnotherRepresentationDoesNotCauseAnIncorrect304(string? initialEncoding, string? nextEncoding)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var initial = await Send(client, AssetPath, initialEncoding);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.NotNull(initial.Headers.ETag);

        // 同一 URL 切换原文与压缩表示，不能把另一表示的强 ETag 误认为已持有本次字节。
        using var request = new HttpRequestMessage(HttpMethod.Get, AssetPath);
        if (nextEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", nextEncoding);
        request.Headers.IfNoneMatch.Add(initial.Headers.ETag!);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.NotEqual(initial.Headers.ETag, response.Headers.ETag);
        if (nextEncoding is null) Assert.Empty(response.Content.Headers.ContentEncoding);
        else Assert.Equal(nextEncoding, Assert.Single(response.Content.Headers.ContentEncoding));
        AssertVaryEncoding(response);
        AssertImmutable(response);
        Assert.Equal(fixture.Read(AssetPath), await ReadDecoded(response));
    }

    [Theory]
    [InlineData("br", ".br")]
    [InlineData("gzip", ".gz")]
    [InlineData(null, "")]
    public async Task HeadReturnsSelectedRepresentationHeadersWithoutBody(string? acceptEncoding, string suffix)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, AssetPath);
        if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(fixture.Read(AssetPath + suffix).Length, response.Content.Headers.ContentLength);
        if (acceptEncoding is null) Assert.Empty(response.Content.Headers.ContentEncoding);
        else Assert.Equal(acceptEncoding, Assert.Single(response.Content.Headers.ContentEncoding));
        AssertVaryEncoding(response);
        AssertImmutable(response);
    }

    [Fact]
    public async Task PublicHashedAssetsAreImmutable()
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, AssetPath, "br");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertImmutable(response);
    }

    [Theory]
    [InlineData("/theme-init.js")]
    [InlineData("/index.html")]
    [InlineData("/assets/app.js")]
    [InlineData("/assets/console-abc12345.js")]
    [InlineData("/console-assets/private.js")]
    public async Task UnfingerprintedAndConsoleResourcesStayNoStore(string path)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root, "127.0.0.1");
        using var client = factory.CreateClient();
        using var response = await Send(client, path, "br");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.CacheControl?.Public ?? false);
    }

    [Theory]
    [InlineData("/assets/console-abc12345.js")]
    [InlineData("/assets/console-abc12345.js.br")]
    [InlineData("/assets/console-abc12345.js.gz")]
    [InlineData("/console-assets/private.js")]
    [InlineData("/console-assets/private.js.br")]
    public async Task ConsoleFilesAndSidecarsAreRejectedBeforeStaticHandling(string path)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, path, "br");
        await ConsoleResponseAssertions.EmptyNotFoundAsync(response);
    }

    [Theory]
    [InlineData(AssetPath + ".br")]
    [InlineData(AssetPath + ".gz")]
    [InlineData("/theme-init.js.br")]
    [InlineData("/THEME-INIT.JS.br")]
    public async Task PublicSidecarsCannotBeRequestedDirectly(string path)
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var response = await Send(client, path, "br");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task PureIpAndVisitorApiKeepOriginalNoStoreProtocol()
    {
        var fixture = new StaticFixture();
        using var factory = new StaticFactory(fixture.Root);
        using var client = factory.CreateClient();
        using var plain = await Send(client, "/", "br, gzip");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("203.0.113.27\n", await plain.Content.ReadAsStringAsync());
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.True(plain.Headers.CacheControl?.NoStore);
        Assert.Contains("Accept", plain.Headers.Vary);

        using var visitor = await Send(client, "/api/ip/me", "br, gzip");
        Assert.Empty(visitor.Content.Headers.ContentEncoding);
        Assert.True(visitor.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await visitor.Content.ReadAsStringAsync());
        Assert.Equal("203.0.113.27", json.RootElement.GetProperty("data").GetProperty("ip").GetString());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await client.SendAsync(request);
    }

    private static async Task<byte[]> ReadDecoded(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.Content.Headers.ContentEncoding.Any()) return bytes;
        using var input = new MemoryStream(bytes);
        using Stream decoder = response.Content.Headers.ContentEncoding.Single() switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            var encoding => throw new InvalidOperationException($"Unexpected encoding: {encoding}")
        };
        using var output = new MemoryStream();
        await decoder.CopyToAsync(output);
        return output.ToArray();
    }

    private static void AssertVaryEncoding(HttpResponseMessage response) =>
        Assert.Contains(response.Headers.Vary, value => value.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase));

    private static void AssertImmutable(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
        Assert.Contains(response.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
    }

    private sealed class StaticFactory(string distPath, string address = "203.0.113.27") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Frontend:DistPath"] = distPath, ["Database:Enabled"] = "false" }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestConnectionFilter(address)));
        }
    }

    private sealed class StaticFixture
    {
        public string Root { get; }

        public StaticFixture()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
            if (repository is null) throw new InvalidOperationException("Cannot locate repository fixture directory.");
            // 每个用例独立目录，遵守项目不自动删除文件的约定，并避免并行测试互相覆盖。
            Root = Path.Combine(repository.FullName, ".local", "verification", "static-assets-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Root);
            var script = string.Concat(Enumerable.Repeat("const message = '受控中文静态资源';\n", 80));
            Write(AssetPath, script);
            Write("/assets/site-def67890.css", string.Concat(Enumerable.Repeat(".fixture { color: #013187; padding: 12px; }\n", 60)));
            Write("/logos/provider.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\">" + string.Concat(Enumerable.Repeat("<text>受控来源图标</text>", 60)) + "</svg>");
            Write("/assets/gzip-only-abc12345.js", script, brotli: false);
            Write("/assets/app.js", script);
            Write("/assets/console-abc12345.js", script);
            Write("/console-assets/private.js", script);
            Write("/theme-init.js", script);
            Write("/index.html", "<!doctype html><html lang=\"zh-CN\"><title>受控首页</title></html>", brotli: false, gzip: false);
        }

        public byte[] Read(string path) => File.ReadAllBytes(Path.Combine(Root, path.TrimStart('/')));

        private void Write(string path, string text, bool brotli = true, bool gzip = true)
        {
            var fullPath = Path.Combine(Root, path.TrimStart('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(fullPath, bytes);
            if (brotli)
            {
                using var output = File.Create(fullPath + ".br");
                using var encoder = new BrotliStream(output, CompressionLevel.SmallestSize);
                encoder.Write(bytes);
            }
            if (gzip)
            {
                using var output = File.Create(fullPath + ".gz");
                using var encoder = new GZipStream(output, CompressionLevel.SmallestSize);
                encoder.Write(bytes);
            }
        }
    }
}
