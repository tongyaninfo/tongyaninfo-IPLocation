using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tongyan.Support.Client.Service;

namespace IpLocation.Api.Tests.Integration;

/// <summary>支持代理使用假远端，不接触生产服务或产品数据库。</summary>
public sealed class SupportApiTests
{
    [Fact]
    public async Task StartingHostAndQueryingIpDoNotCreateSupportIdentityOrContactRemote()
    {
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-passive-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "unknown", directory, "server", new Uri("https://support.invalid")), http);
        await using var factory = new ProtocolFactory()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(support))));
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        using var address = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, address.StatusCode);
        Assert.Empty(remote.Paths);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task AnonymousFeedbackUsesPerTicketReceiptAndNeverReturnsInstallationSecret()
    {
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-api-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "1.0.0", directory, "server", new Uri("https://support.invalid")), http);
        await using var factory = new ProtocolFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(support))));
        using var client = factory.CreateClient();
        var receipt = new string('b', 64);
        var requestId = Guid.NewGuid();
        using var submitted = await client.PostAsJsonAsync("/api/support/feedback", new
        { clientRequestId = requestId, receiptToken = receipt, category = "bug", description = "受控反馈", attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        Assert.DoesNotContain("installationSecret", await submitted.Content.ReadAsStringAsync());
        using var lookup = await client.PostAsJsonAsync("/api/support/feedback/by-request/" + requestId + "/lookup", new { receiptToken = receipt });
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
        Assert.Equal([
            "/api/products/v1/installations/register",
            "/api/feedback/v1/tickets",
            $"/api/feedback/v1/tickets/by-request/{requestId:D}/receipt"
        ], remote.Paths);
        Assert.Equal(receipt, JsonDocument.Parse(remote.LastBody!).RootElement.GetProperty("receiptToken").GetString());
    }

    [Fact]
    public async Task CrossOriginUploadAndInvalidRequestAreRejectedBeforeRemoteCall()
    {
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-denied-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "1.0.0", directory, "server", new Uri("https://support.invalid")), http);
        await using var factory = new ProtocolFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(support))));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/support/feedback")
        { Content = JsonContent.Create(new { clientRequestId = Guid.NewGuid(), receiptToken = new string('b', 64), category = "bug", description = "受控反馈", attachmentIds = Array.Empty<Guid>() }) };
        request.Headers.Add("Origin", "https://untrusted.invalid");
        using var denied = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/support/feedback", new { clientRequestId = Guid.Empty, receiptToken = "invalid", category = "bug", attachmentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(remote.Paths);
    }

    private sealed class SupportRemote : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public byte[]? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            LastBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true, code = "OK", message = "受控结果", data = new { feedbackId = "9007199254740993", status = "pending" } }) };
        }
    }
}
