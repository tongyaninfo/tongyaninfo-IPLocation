using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Controllers.Console;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>读取资源边界、统计口径和二次脱敏；不连接或改动真实数据库。</summary>
public sealed class ConsoleReadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public void WindowLimitsAndRetentionAreExplicitAndOverviewUsesCompleteMinuteBuckets()
    {
        var window = ConsoleReadRules.NormalizeWindow(new ConsoleReadWindowRequest
        {
            FromUtc = Now.AddDays(-31), ToUtc = Now.AddHours(2)
        }, Now, 30);
        Assert.Equal(Now.AddDays(-30), window.FromUtc);
        Assert.Equal(Now, window.ToUtc);
        var rounded = ConsoleReadRules.NormalizeWindow(new ConsoleReadWindowRequest { FromUtc = Now.AddMinutes(-3) }, Now, 180, true);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 11, 57, 0, TimeSpan.Zero), rounded.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 12, 1, 0, TimeSpan.Zero), rounded.ToUtc);
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.NormalizeWindow(
            new ConsoleReadWindowRequest { FromUtc = Now.AddDays(-32) }, Now, 180));
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.NormalizeWindow(
            new ConsoleReadWindowRequest { FromUtc = Now.AddDays(-32), ToUtc = Now.AddDays(-31) }, Now, 30));
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.PageOffset(0, 20));
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.PageOffset(1001, 20));
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.PageOffset(1, 101));
        Assert.Equal(40, ConsoleReadRules.PageOffset(3, 20));
    }

    [Fact]
    public void PercentileUsesMergedCountsAndNeverAveragesMinutePercentiles()
    {
        var histogram = new long[13];
        histogram[0] = 94;
        histogram[6] = 6;
        Assert.Equal(250, ConsoleReadRules.Percentile95(histogram));
        histogram[6] = 0;
        histogram[12] = 6;
        Assert.Null(ConsoleReadRules.Percentile95(histogram));
        Assert.Null(ConsoleReadRules.Percentile95(new long[13]));
        Assert.Null(ConsoleReadRules.Percentile95([-1]));
    }

    [Fact]
    public void AuditDifferenceAllowsKnownNestedSettingsAndRemovesSecretsAndPaths()
    {
        var safe = ConsoleReadRules.SanitizeDifference("""
            {"before":{"query":{"defaultDatabase":"geolite2","token":"secret-one"},"allowedNetworks":["127.0.0.1"],"providerIds":["dbip","chunzhen"]},
             "after":{"enabled":false,"providerIds":["chunzhen","dbip"],"databasePath":"/private/database","password":"secret-two"},
             "connectionString":"secret-three","fileName":"/private/file","value":"secret-four"}
            """);
        var serialized = JsonSerializer.Serialize(safe);
        Assert.Contains("geolite2", serialized);
        Assert.Contains("127.0.0.1", serialized);
        Assert.Contains("enabled", serialized);
        Assert.Equal(new[] { "dbip", "chunzhen" }, safe["before"].GetProperty("providerIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "chunzhen", "dbip" }, safe["after"].GetProperty("providerIds").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("secret", serialized);
        Assert.DoesNotContain("private", serialized);
        Assert.Empty(ConsoleReadRules.SanitizeDifference("invalid-json"));
        Assert.Empty(ConsoleReadRules.SanitizeDifference("[]"));
    }

    [Fact]
    public async Task InvalidReadRequestsAreRejectedBeforeCreatingDatabaseContext()
    {
        var factory = new UnavailableContextFactory();
        using var services = new ServiceCollection().AddSingleton<IDbContextFactory<IpLocationDbContext>>(factory).BuildServiceProvider();
        var reads = Reader(services);
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { SourceAddress = "https://example.com", PageSize = 20 }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { RouteCategory = "arbitrary-query-target" }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { StatusCode = 999 }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAuditsAsync(
            new ConsoleAuditReadRequest { TraceId = new string('x', 129) }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { RouteCategory = "mcp", ToolName = new string('x', 65) }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { RouteCategory = "mcp", ResultCode = "error\ncode" }, CancellationToken.None));
        await Assert.ThrowsAsync<ConsoleReadValidationException>(() => reads.GetAccessLogsAsync(
            new ConsoleAccessLogReadRequest { RouteCategory = "mcp", TraceId = new string('x', 129) }, CancellationToken.None));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task MissingPersistenceNeverReturnsAnInMemoryWindowAsHistoricalSuccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var reads = Reader(services);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reads.GetOverviewAsync(new(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reads.GetAccessLogsAsync(new(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reads.GetAuditsAsync(new(), CancellationToken.None));
    }

    [Fact]
    public async Task ControllerRejectsAnonymousBeforeReadingAndSanitizesStorageExceptions()
    {
        var reads = new ControlledReader();
        var identities = new ControlledIdentity();
        var controller = new ConsoleReadController(reads, identities, null!, NullLogger<ConsoleReadController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var denied = Assert.IsType<ObjectResult>(await controller.AccessLogs(new()));
        Assert.Equal(401, denied.StatusCode);
        Assert.Equal(0, reads.Calls);
        identities.Allowed = true;
        var unavailable = Assert.IsType<ObjectResult>(await controller.AccessLogs(new()));
        Assert.Equal(503, unavailable.StatusCode);
        var body = Assert.IsType<ApiResult<object>>(unavailable.Value);
        Assert.Equal("CONSOLE_STORAGE_UNAVAILABLE", body.Code);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(body));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal(1, reads.Calls);
    }

    private static ConsoleReadService Reader(IServiceProvider services)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AccessLogOptions());
        var clock = new FixedClock();
        var queue = new AccessEventQueue(options, clock);
        return new ConsoleReadService(services, new InMemoryAccessLogStore(options, queue, clock), queue,
            options, Microsoft.Extensions.Options.Options.Create(new PersistentAccessLogOptions()), clock);
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class UnavailableContextFactory : IDbContextFactory<IpLocationDbContext>
    {
        public int Calls { get; private set; }
        public IpLocationDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("unavailable"); }
        public Task<IpLocationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("unavailable"); }
    }

    private sealed class ControlledReader : IConsoleReadService
    {
        public int Calls { get; private set; }
        public Task<ConsoleAccessLogPage> GetAccessLogsAsync(ConsoleAccessLogReadRequest request, CancellationToken cancellationToken)
        { Calls++; throw new ArgumentException("connectionString=secret-test-fixture"); }
        public Task<ConsoleOverviewResponse> GetOverviewAsync(ConsoleReadWindowRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ConsoleAuditPage> GetAuditsAsync(ConsoleAuditReadRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class ControlledIdentity : IConsoleIdentityService
    {
        public bool Allowed { get; set; }
        public Task<ConsoleOperationResponse<ConsoleAccessResponse>> GetAccessAsync(HttpContext context) => Task.FromResult(
            new ConsoleOperationResponse<ConsoleAccessResponse>(Allowed ? 200 : 401, Allowed
                ? ApiResult<ConsoleAccessResponse>.Ok(new(true, true, true, "fixture", null))
                : ApiResult<ConsoleAccessResponse>.Fail("CONSOLE_UNAUTHENTICATED", "fixture")));
        public Task<ConsoleOperationResponse<ConsoleAccessResponse>> LoginAsync(HttpContext context, ConsoleLoginRequest request) => throw new NotImplementedException();
        public Task<ConsoleOperationResponse<object>> LogoutAsync(HttpContext context) => throw new NotImplementedException();
    }
}
