using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Integration;

/// <summary>运行快照与真实 Cookie/防伪管线的集成验证；身份和审计使用受控内存存储，不连接PG。</summary>
public sealed class ConsoleBasicSettingsTests
{
    private const string AuthCookieName = "Iplocation.Console.Auth=";

    [Fact]
    public async Task DisabledAccessLoggingKeepsReliableLoginAndReportsRequestedAndEffectiveQueueCapacity()
    {
        var identities = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = Factory(identities);
        using var client = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<IConsoleRuntimeSettings>();
        var queue = factory.Services.GetRequiredService<IAccessEventQueue>();
        var effectiveCapacity = queue.Capacity;
        var requestedCapacity = effectiveCapacity + 37;
        var current = runtime.Current;
        runtime.Publish(current with
        {
            Revision = current.Revision + 1,
            Basic = new ConsoleBasicSettings(0, true, 17, requestedCapacity)
        });

        var before = queue.GetMetrics();
        using var plain = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("127.0.0.1\n", await plain.Content.ReadAsStringAsync());
        var after = queue.GetMetrics();
        Assert.Equal(before.Received, after.Received);
        Assert.Equal(before.Accepted, after.Accepted);

        // 普通访问关闭后，管理员仍须经过真实防伪校验及可靠登录审计再获得 Cookie。
        using var login = await Login(client);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(1, identities.SuccessfulLogins);
        Assert.Contains(identities.Audits, audit => audit.Action == "Admin.Login" && audit.Result == "success");
        var ticket = ReadTicket(factory.Services, login);
        Assert.Equal(TimeSpan.FromMinutes(17), ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc);

        using var response = await client.GetAsync("/api/console/settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        var basic = data.GetProperty("basic");
        Assert.Equal(0, basic.GetProperty("accessLogRetentionDays").GetInt32());
        Assert.True(basic.GetProperty("retainFullSourceIp").GetBoolean());
        Assert.Equal(17, basic.GetProperty("sessionMinutes").GetInt32());
        Assert.Equal(requestedCapacity, basic.GetProperty("queueCapacity").GetInt32());
        var actual = data.GetProperty("runtime");
        Assert.Equal(0, actual.GetProperty("recentEventRetentionDays").GetInt32());
        Assert.Equal(17, actual.GetProperty("sessionMinutes").GetInt32());
        Assert.Equal(effectiveCapacity, actual.GetProperty("queueCapacity").GetInt32());
        Assert.True(actual.GetProperty("queueCapacityRequiresRestart").GetBoolean());
        Assert.Equal(effectiveCapacity, queue.Capacity);
    }

    [Fact]
    public async Task SessionDurationChangesAffectNewLoginsWithoutChangingExistingTicketExpiry()
    {
        var identities = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = Factory(identities);
        using var originalClient = factory.CreateClient();
        var runtime = factory.Services.GetRequiredService<IConsoleRuntimeSettings>();
        var queue = factory.Services.GetRequiredService<IAccessEventQueue>();
        var initial = runtime.Current;
        runtime.Publish(initial with
        {
            Revision = initial.Revision + 1,
            Basic = new ConsoleBasicSettings(0, true, 17, queue.Capacity)
        });
        using var originalLogin = await Login(originalClient);
        Assert.Equal(HttpStatusCode.OK, originalLogin.StatusCode);
        var originalTicket = ReadTicket(factory.Services, originalLogin);
        var originalExpiry = originalTicket.Properties.ExpiresUtc;
        Assert.Equal(TimeSpan.FromMinutes(17), originalExpiry - originalTicket.Properties.IssuedUtc);

        var changed = runtime.Current;
        runtime.Publish(changed with { Revision = changed.Revision + 1, Basic = changed.Basic! with { SessionMinutes = 41 } });
        using var access = await originalClient.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        Assert.False(access.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(value => value.StartsWith(AuthCookieName, StringComparison.Ordinal)));
        Assert.Equal(originalExpiry, ReadTicket(factory.Services, originalLogin).Properties.ExpiresUtc);

        using var newClient = factory.CreateClient();
        using var newLogin = await Login(newClient);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        var newTicket = ReadTicket(factory.Services, newLogin);
        Assert.Equal(TimeSpan.FromMinutes(41), newTicket.Properties.ExpiresUtc - newTicket.Properties.IssuedUtc);
        Assert.Equal(2, identities.SuccessfulLogins);
        Assert.Equal(2, identities.Audits.Count(audit => audit.Action == "Admin.Login" && audit.Result == "success"));
    }

    private static WebApplicationFactory<Program> Factory(ControlledIdentityStore identities) =>
        new ProtocolFactory(connectionAddress: "127.0.0.1", identities: identities).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Database:Enabled"] = "false" })));

    private static async Task<HttpResponseMessage> Login(HttpClient client)
    {
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        {
            Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password })
        };
        request.Headers.Add("X-CSRF-TOKEN", json.RootElement.GetProperty("data").GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private static AuthenticationTicket ReadTicket(IServiceProvider services, HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AuthCookieName, StringComparison.Ordinal));
        var protectedTicket = cookie[AuthCookieName.Length..].Split(';', 2)[0];
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ConsoleAuthentication.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(protectedTicket);
        Assert.NotNull(ticket);
        Assert.NotNull(ticket.Properties.IssuedUtc);
        Assert.NotNull(ticket.Properties.ExpiresUtc);
        return ticket;
    }
}
