using System.Net;
using IpLocation.Api.Tests.Common;
using System.Net.Http.Json;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Integration;

/// <summary>真实 Cookie/防伪/管线集成验证；使用受控身份存储，不连接真实数据库。</summary>
public sealed class ConsoleAuthenticationTests
{
    [Fact]
    public async Task UninitializedAndAnonymousStatesAreDistinct()
    {
        var store = new ControlledIdentityStore();
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var uninitialized = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, uninitialized.StatusCode);
        using var first = await Json(uninitialized);
        Assert.Equal("CONSOLE_NOT_INITIALIZED", first.RootElement.GetProperty("code").GetString());
        Assert.False(first.RootElement.GetProperty("data").GetProperty("initialized").GetBoolean());

        store.Account = ControlledIdentityStore.CreateAccount();
        using var anonymous = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var second = await Json(anonymous);
        Assert.Equal("CONSOLE_UNAUTHENTICATED", second.RootElement.GetProperty("code").GetString());
        Assert.True(second.RootElement.GetProperty("data").GetProperty("initialized").GetBoolean());
        Assert.False(anonymous.Headers.Contains("Location"));
    }

    [Fact]
    public async Task LoginRequiresCsrfAndReliableAuditBeforeIssuingCookie()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var missing = await Login(client, null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, store.SuccessfulLogins);
        Assert.False(missing.Headers.TryGetValues("Set-Cookie", out _));

        var token = await Csrf(client);
        store.FailWrites = true;
        using var failedAudit = await Login(client, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedAudit.StatusCode);
        Assert.False(failedAudit.Headers.TryGetValues("Set-Cookie", out _));
        Assert.Equal(0, store.SuccessfulLogins);
    }

    [Fact]
    public async Task ValidLoginReturnsStringIdAndHttpOnlyStrictCookie()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var response = await Login(client, await Csrf(client));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = AuthCookie(response);
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/api/console", cookie.ToLowerInvariant());
        using var json = await Json(response);
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("data").GetProperty("user").GetProperty("id").ValueKind);
        Assert.DoesNotContain(ControlledIdentityStore.Password, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, store.SuccessfulLogins);
        using var access = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountDisableOrSecurityVersionChangeRevokesExistingCookieImmediately(bool disable)
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var login = await Login(client, await Csrf(client));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        if (disable) store.Account.Enabled = false;
        else store.Account.SecurityVersion++;
        using var access = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.Unauthorized, access.StatusCode);
    }

    [Fact]
    public async Task LogoutRevokesCopiedCookieAndAuditFailureDoesNotClaimLogoutSuccess()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var login = await Login(client, await Csrf(client));
        var copiedCookie = AuthCookie(login).Split(';', 2)[0];
        var token = await Csrf(client);
        using var invalidCsrf = await Logout(client, "invalid-csrf-fixture");
        Assert.Equal(HttpStatusCode.BadRequest, invalidCsrf.StatusCode);
        Assert.Equal(1, store.Account.SecurityVersion);
        store.FailWrites = true;
        using var failed = await Logout(client, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(1, store.Account.SecurityVersion);

        store.FailWrites = false;
        using var success = await Logout(client, token);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(2, store.Account.SecurityVersion);
        using var replayClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/console/access");
        request.Headers.Add("Cookie", copiedCookie);
        using var replay = await replayClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task PublicRootWithManuallyAttachedValidCookieNeverReadsIdentityStore()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        using var login = await Login(client, await Csrf(client));
        var cookie = AuthCookie(login).Split(';', 2)[0];
        var reads = store.Reads;
        store.FailReads = true;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("127.0.0.1\n", await response.Content.ReadAsStringAsync());
        Assert.Equal(reads, store.Reads);
        using var console = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, console.StatusCode);
    }

    [Fact]
    public async Task UnknownAndWrongPasswordReturnSameErrorAndWriteFailureAudit()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        var token = await Csrf(client);
        using var unknown = await Login(client, token, account: "unknown-fixture");
        using var wrong = await Login(client, token, password: "wrong test-only password");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var first = await Json(unknown);
        using var second = await Json(wrong);
        Assert.Equal(first.RootElement.GetProperty("code").GetString(), second.RootElement.GetProperty("code").GetString());
        Assert.Equal(first.RootElement.GetProperty("message").GetString(), second.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, store.Audits.Count);
        Assert.Null(store.Audits[0].ActorId);
        Assert.All(store.Audits, audit => Assert.Equal("denied", audit.Result));
    }

    [Theory]
    [InlineData("192.0.2.25")]
    [InlineData("198.51.100.25")]
    public async Task AllowedNonLoopbackHttpSupportsCsrfLoginAndLogout(string address)
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: address, identities: store);
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        var csrfCookie = csrf.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("Iplocation.Console.Csrf=", StringComparison.Ordinal));
        Assert.DoesNotContain("; secure", csrfCookie.ToLowerInvariant());
        using var tokenJson = await Json(csrf);
        var token = tokenJson.RootElement.GetProperty("data").GetProperty("token").GetString()!;
        using var missingCsrf = await Login(client, null);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        Assert.Equal(0, store.SuccessfulLogins);

        using var login = await Login(client, token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain("; secure", AuthCookie(login).ToLowerInvariant());
        Assert.Contains("httponly", AuthCookie(login).ToLowerInvariant());
        Assert.Contains("samesite=strict", AuthCookie(login).ToLowerInvariant());
        using var access = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);

        token = await Csrf(client);
        using var invalidLogout = await Logout(client, "invalid-csrf-fixture");
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogout.StatusCode);
        Assert.Equal(1, store.Account.SecurityVersion);
        using var logout = await Logout(client, token);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        using var signedOut = await client.GetAsync("/api/console/access");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        Assert.Equal(2, store.Account.SecurityVersion);
    }

    [Fact]
    public async Task HttpOutsideAllowedNetworksRemainsDeniedBeforeIdentityOrCsrf()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "203.0.113.27", identities: store);
        using var client = factory.CreateClient();
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        using var login = await Login(client, null);
        using var logout = await Logout(client, "invalid-csrf-fixture");
        await ConsoleResponseAssertions.EmptyNotFoundAsync(csrf);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(login);
        await ConsoleResponseAssertions.EmptyNotFoundAsync(logout);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.SuccessfulLogins);
    }

    [Fact]
    public async Task HttpsLoginSetsSecureCookieAndAuthenticatedUnknownApiReturns501()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "192.0.2.25", identities: store);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var csrf = await client.GetAsync("/api/console/auth/csrf");
        Assert.Contains("; secure", csrf.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("Iplocation.Console.Csrf=", StringComparison.Ordinal)).ToLowerInvariant());
        using var login = await Login(client, await Csrf(client));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("secure", AuthCookie(login).ToLowerInvariant());
        using var unknown = await client.GetAsync("/api/console/unimplemented");
        Assert.Equal(HttpStatusCode.NotImplemented, unknown.StatusCode);
        using var json = await Json(unknown);
        Assert.Equal("CONSOLE_NOT_IMPLEMENTED", json.RootElement.GetProperty("code").GetString());

        // Cookie 容器不会将 HTTPS 的 Secure 会话发送给 HTTP，后端仍按匿名身份返回。
        using var insecureAccess = await client.GetAsync("http://localhost/api/console/access");
        Assert.Equal(HttpStatusCode.Unauthorized, insecureAccess.StatusCode);
        using var insecureJson = await Json(insecureAccess);
        Assert.Equal("CONSOLE_UNAUTHENTICATED", insecureJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task LoginAttemptsAreBoundedWithoutQueueing()
    {
        var store = new ControlledIdentityStore { Account = ControlledIdentityStore.CreateAccount() };
        using var factory = new ProtocolFactory(connectionAddress: "127.0.0.1", identities: store);
        using var client = factory.CreateClient();
        var token = await Csrf(client);
        for (var index = 0; index < 10; index++)
        {
            using var denied = await Login(client, token, password: "wrong test-only password");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var limited = await Login(client, token, password: "wrong test-only password");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/console/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var json = await Json(response);
        return json.RootElement.GetProperty("data").GetProperty("token").GetString()!;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string? token, string account = "fixture-admin",
        string password = ControlledIdentityStore.Password)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/login")
        {
            Content = JsonContent.Create(new { account, password })
        };
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Logout(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/console/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    private static string AuthCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(cookie => cookie.StartsWith("Iplocation.Console.Auth=", StringComparison.Ordinal));

    private static async Task<JsonDocument> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}

internal sealed class ControlledIdentityStore : IConsoleIdentityStore
{
    public const string Password = "Test-only fixture password 123!";
    private static readonly string PasswordHash = new PasswordHasher<AdminAccount>(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = ConsoleAuthentication.PasswordIterations })).HashPassword(new(), Password);
    public AdminAccount? Account { get; set; }
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public int Reads { get; private set; }
    public int SuccessfulLogins { get; private set; }
    public List<AdminAuditEvent> Audits { get; } = [];

    public static AdminAccount CreateAccount() => new()
    {
        Id = 9007199254740993, Account = "fixture-admin", NormalizedAccount = "FIXTURE-ADMIN", DisplayName = "受控测试管理员",
        Enabled = true, PasswordHash = PasswordHash, SecurityVersion = 1
    };

    public Task<bool> HasAccountsAsync(CancellationToken cancellationToken)
    {
        Read(); return Task.FromResult(Account is not null);
    }

    public Task<AdminAccount?> FindByAccountAsync(string normalizedAccount, CancellationToken cancellationToken)
    {
        Read(); return Task.FromResult(Account?.NormalizedAccount == normalizedAccount ? Account : null);
    }

    public Task<AdminAccount?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        Read(); return Task.FromResult(Account?.Id == id ? Account : null);
    }

    public Task WriteAuditAsync(AdminAuditEvent audit, CancellationToken cancellationToken)
    {
        Write(); Audits.Add(audit); return Task.CompletedTask;
    }

    public Task<bool> CommitLoginAsync(AdminAccount expectedAccount, string? replacementHash, AdminAuditEvent audit,
        CancellationToken cancellationToken)
    {
        Write(); SuccessfulLogins++; Audits.Add(audit); return Task.FromResult(true);
    }

    public Task<bool> LogoutAsync(long accountId, long expectedVersion, AdminAuditEvent audit, CancellationToken cancellationToken)
    {
        Write(); Account!.SecurityVersion++; Audits.Add(audit); return Task.FromResult(true);
    }

    public Task<bool> ResetCredentialsAsync(AdminAccount expectedAccount, string accountName, string passwordHash,
        AdminAuditEvent audit, CancellationToken cancellationToken) => throw new NotSupportedException("Local reset is not an HTTP operation.");

    public Task<bool> InitializeAsync(AdminAccount account, AdminAuditEvent audit, CancellationToken cancellationToken) =>
        throw new NotSupportedException("初始化事务由获准 PostgreSQL 验收验证。");

    private void Read() { Reads++; if (FailReads) throw new InvalidOperationException("受控存储故障"); }
    private void Write() { if (FailWrites) throw new InvalidOperationException("受控审计故障"); }
}
