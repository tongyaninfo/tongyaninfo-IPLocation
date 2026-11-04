using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IpLocation.Api.Tests.Integration;

/// <summary>真实HTTP管线验证所有业务端点，使用受控身份，不创建数据库或测试账号。</summary>
public sealed class ConsoleManagementTests
{
    [Theory]
    [InlineData("overview")]
    [InlineData("databases")]
    [InlineData("access-logs")]
    [InlineData("audits")]
    [InlineData("settings")]
    public async Task BusinessReadsRequireAuthenticatedAdministrator(string endpoint)
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/console/{endpoint}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SettingsExposeOnlyNonSensitiveRuntimeAndWriteRequiresCsrf()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        using var tokenJson = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        var token = tokenJson.RootElement.GetProperty("data").GetProperty("token").GetString();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        { Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password }) };
        login.Headers.Add("X-CSRF-TOKEN", token);
        using var loggedIn = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        using var settings = await client.GetAsync("/api/console/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var text = await settings.Content.ReadAsStringAsync();
        Assert.DoesNotContain("connectionString", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyRingPath", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        using var data = JsonDocument.Parse(text);
        Assert.Equal("127.0.0.1", data.RootElement.GetProperty("data").GetProperty("currentSourceAddress").GetString());
        using var saved = await client.PostAsJsonAsync("/api/console/settings", new
        {
            revision = 1, query = new { defaultDatabase = "geolite2", providerTimeoutMs = 2500, totalTimeoutMs = 5000, maxConcurrentSources = 4 },
            allowedNetworks = new[] { "127.0.0.1" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, saved.StatusCode);
        using var rejected = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        Assert.Equal("CONSOLE_CSRF_INVALID", rejected.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReadHistoryDoesNotMasqueradeAsMemoryWhenDatabaseDisabled()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        using var tokenJson = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        { Content = JsonContent.Create(new { account = "fixture-admin", password = ControlledIdentityStore.Password }) };
        login.Headers.Add("X-CSRF-TOKEN", tokenJson.RootElement.GetProperty("data").GetProperty("token").GetString());
        using var authenticated = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        foreach (var endpoint in new[] { "overview", "access-logs", "audits" })
        {
            using var response = await client.GetAsync($"/api/console/{endpoint}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("memory", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
