using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IpLocation.Api.Tests.Integration;

/// <summary>真实 MVC 管线加 multipart HTTP 请求，防止参数绑定提前消费流式上传正文。</summary>
public sealed class StreamingUploadTests
{
    [Theory]
    [InlineData("ipinfo", ".mmdb")]
    [InlineData("chunzhen", ".dat")]
    [InlineData("CZ88", ".dat")]
    [InlineData("ip2region", ".xdb")]
    public async Task MultipartHttpBodyNeedsOnlyFileAndAddressFamily(string id, string extension)
    {
        using var factory = new UploadProbeFactory();
        using var client = factory.CreateClient();
        var payload = new byte[2_000_123];
        Random.Shared.NextBytes(payload);
        using var content = CreateForm(payload, extension);
        using var response = await client.PostAsync($"/upload-probe/stream/{id}", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(payload.Length, json.RootElement.GetProperty("receivedBytes").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), json.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("dual", json.RootElement.GetProperty("fields").GetProperty("addressFamily").GetString());
        Assert.False(json.RootElement.GetProperty("fields").TryGetProperty("licenseConfirmed", out _));
        Assert.False(json.RootElement.GetProperty("fields").TryGetProperty("licenseEvidence", out _));
    }

    [Fact]
    public async Task MultipartHttpReaderRejectsExtraSection()
    {
        using var factory = new UploadProbeFactory();
        using var client = factory.CreateClient();
        using var content = CreateForm([1, 2, 3]);
        content.Add(new StringContent("extra"), "extra");
        using var response = await client.PostAsync("/upload-probe/stream/ipinfo", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("INVALID_UPLOAD_FIELDS", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FileCreationSetsPrivateModeBeforeExclusiveOpen()
    {
        // 遵守不自动删除约定，测试产物留在系统临时目录中。
        var directory = Path.Combine(Path.GetTempPath(), "Iplocation-upload-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetRandomFileName());
        await using (var output = DatabaseManagementService.CreateUploadFile(path)) await output.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(3, new FileInfo(path).Length);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private static MultipartFormDataContent CreateForm(byte[] payload, string extension = ".mmdb")
    {
        var content = new MultipartFormDataContent("----formdata-undici-controlled-boundary");
        content.Add(new ByteArrayContent(payload), "file", "controlled" + extension);
        content.Add(new StringContent("dual"), "addressFamily");
        return content;
    }
    private sealed class UploadProbeFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
            services.AddControllers().AddApplicationPart(typeof(UploadProbeController).Assembly));
    }
}

[ApiController]
[Route("upload-probe")]
public sealed class UploadProbeController : ControllerBase
{
    [HttpPost("stream/{id}")]
    [DisableFormValueModelBinding]
    public async Task<IActionResult> Stream([FromRoute] string id, CancellationToken cancellationToken)
    {
        var media = Microsoft.Net.Http.Headers.MediaTypeHeaderValue.Parse(Request.ContentType!);
        try
        {
            using var output = new MemoryStream();
            var result = await DatabaseManagementService.ReceiveMultipartAsync(Request.Body,
                Microsoft.Net.Http.Headers.HeaderUtilities.RemoveQuotes(media.Boundary).Value!, ProviderCodes.Public(id) == ProviderCodes.Cz88 ? ".dat" : id == "ip2region" ? ".xdb" : ".mmdb", 10_000_000, 10_000_000,
                () => output, (_, _) => { }, cancellationToken);
            return Ok(new { id, result.ReceivedBytes, result.Sha256, result.Fields });
        }
        catch (DatabaseManagementService.UploadRejectedException exception) { return BadRequest(new { exception.Code }); }
    }
}
