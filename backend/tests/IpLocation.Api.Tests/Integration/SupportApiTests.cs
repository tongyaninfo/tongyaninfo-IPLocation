using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tongyan.Support.Client.Models;
using Tongyan.Support.Client.Service;

namespace IpLocation.Api.Tests.Integration;

/// <summary>支持代理使用假远端，不接触生产服务或产品数据库。</summary>
public sealed class SupportApiTests
{
    [Fact]
    public async Task TelemetryChangeRequiresReliableIntentAuditAndRecordsActualOutcome()
    {
        var identities = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-audit-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "unknown", directory, "server", new Uri("https://support.invalid")), http);
        await using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: identities)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(support))));
        using var client = factory.CreateClient();
        async Task<string> Csrf()
        {
            using var response = await client.GetAsync("/api/console/auth/csrf");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        }
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        { Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password }) };
        login.Headers.Add("X-CSRF-TOKEN", await Csrf());
        using var loggedIn = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        var token = await Csrf();
        async Task<HttpResponseMessage> Toggle()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/console/support/telemetry")
            { Content = JsonContent.Create(new { enabled = false }) };
            request.Headers.Add("X-CSRF-TOKEN", token);
            return await client.SendAsync(request);
        }
        identities.FailWrites = true;
        using var denied = await Toggle();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.True((await support.StatusAsync(true, default)).TelemetryEnabled);
        identities.FailWrites = false;
        using var allowed = await Toggle();
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.False((await support.StatusAsync(true, default)).TelemetryEnabled);
        Assert.Contains(identities.Audits, item => item.Action == "Support.Telemetry.Update" && item.Result == "intent");
        Assert.Contains(identities.Audits, item => item.Action == "Support.Telemetry.Update" && item.Result == "success");
    }

    [Fact]
    public async Task AnonymousFeedbackUsesPerTicketReceiptAndNeverReturnsInstallationSecret()
    {
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-api-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "1.0.0", directory, "server", new Uri("https://support.invalid"), false), http);
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
        Assert.Contains("/by-request/", remote.LastPath);
        Assert.DoesNotContain("?", remote.LastPath);
        Assert.Equal(receipt, JsonDocument.Parse(remote.LastBody!).RootElement.GetProperty("receiptToken").GetString());
        Assert.DoesNotContain(remote.Paths, path => path.EndsWith("/heartbeats"));
        // 公共访客不能借支持接口读取整个安装的配置或反馈列表。
        using var hidden = await client.GetAsync("/api/console/support/status");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task CrossOriginUploadAndInvalidRequestAreRejectedBeforeRemoteCall()
    {
        using var remote = new SupportRemote();
        using var http = new HttpClient(remote);
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ip-support-denied-" + Guid.NewGuid().ToString("N"));
        var support = new ProductSupportClient(new("ip", "1.0.0", directory, "server", new Uri("https://support.invalid"), false), http);
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
        public string LastPath => Paths.Last();
        public byte[]? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            LastBody = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { success = true, code = "OK", message = "受控结果", data = new { feedbackId = "9007199254740993", status = "pending" } }) };
        }
    }
}
