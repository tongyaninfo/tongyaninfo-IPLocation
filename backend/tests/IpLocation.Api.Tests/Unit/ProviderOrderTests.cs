using System.Net;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class ProviderOrderTests
{
    [Fact]
    public void CompleteOrderRejectsMissingDuplicateUnknownAndNullIds()
    {
        string[] known = ["dbip", "ipinfo", "chunzhen"];
        Assert.True(DatabaseManagementService.IsCompleteOrder(["chunzhen", "dbip", "ipinfo"], known));
        Assert.True(DatabaseManagementService.IsCompleteOrder(["CZ88", "dbip", "ipinfo"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(["CZ88", "chunzhen", "ipinfo"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(["dbip", "ipinfo"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(["dbip", "dbip", "chunzhen"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(["dbip", "ipinfo", "unknown"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(["dbip", null!, "chunzhen"], known));
        Assert.False(DatabaseManagementService.IsCompleteOrder(null, known));
    }

    [Fact]
    public void SavedOrderRestoresKnownIdsAndAppendsNewlyRegisteredSources()
    {
        var json = JsonSerializer.Serialize(new[] { "removed", "ipinfo", "ipinfo", "dbip", "chunzhen" });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var restored = CreateRegistry(services, new OrderedProvider("dbip"), new OrderedProvider("ipinfo"), new OrderedProvider("chunzhen"));
        restored.PublishOrder(JsonSerializer.Deserialize<string[]>(json)!, 7);
        Assert.Equal(7, restored.Order.Revision);
        Assert.Equal(new[] { "ipinfo", "dbip", "CZ88" }, Ids(restored));
        Assert.Throws<NotSupportedException>(() => ((IList<IIpDataProvider>)restored.Providers)[0] = new OrderedProvider("replaced"));
    }

    [Fact]
    public void LatePublishedPersistedRevisionCannotReplaceNewlyCommittedOrder()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var registry = CreateRegistry(services, new OrderedProvider("dbip"), new OrderedProvider("ipinfo"));
        registry.PublishOrder(["ipinfo", "dbip"], 2);
        var committed = registry.Order;
        registry.PublishOrder(["dbip", "ipinfo"], 1);
        Assert.Same(committed, registry.Order);
        Assert.Equal(new[] { "ipinfo", "dbip" }, Ids(registry));
    }

    [Fact]
    public async Task InFlightQueryKeepsItsOrderAndNextQueryUsesPublishedOrder()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var delayed = new OrderedProvider("dbip", delayFirst: true);
        using var registry = CreateRegistry(services, delayed, new OrderedProvider("ipinfo"), new OrderedProvider("chunzhen"));
        var query = new IpQueryService(registry.Providers, OptionsFactory.Create(new QueryOptions()), registry);
        var first = query.QueryAsync("8.8.8.8", "all", default);
        await delayed.Entered.Task;
        registry.PublishOrder(["chunzhen", "ipinfo", "dbip"], 1);
        delayed.Continue.TrySetResult();
        Assert.Equal(new[] { "dbip", "ipinfo", "CZ88" }, (await first).Result.Data!.Sources.Select(source => source.Id));
        Assert.Equal(new[] { "CZ88", "ipinfo", "dbip" }, query.GetProviders().Select(source => source.Id));
        var next = await query.QueryAsync("8.8.8.8", "all", default);
        Assert.Equal(new[] { "CZ88", "ipinfo", "dbip" }, next.Result.Data!.Sources.Select(source => source.Id));
    }

    [Fact]
    public void ReorderAuditKeepsReadableBeforeAndAfterArraysWithoutConnectingStorage()
    {
        // 只使用离线EF模型和本地跟踪，不打开连接、不提交或修改任何数据库。
        using var db = new IpLocationDbContext(new DbContextOptionsBuilder<IpLocationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=offline_order_audit").Options);
        var context = new DefaultHttpContext();
        context.Items[ConsoleAuthentication.AccountKey] = new AdminAccount { Id = 10 };
        ConsoleMutation.AddAudit(db, new FixedIds(), new FixedAddress(), context, "database.reorder", "providerOrder", null,
            new { before = new { providerIds = new[] { "dbip", "ipinfo" } }, after = new { providerIds = new[] { "ipinfo", "dbip" } }, revision = 2 });
        var audit = db.AdminAuditEvents.Local.Single();
        Assert.Equal("database.reorder", audit.Action);
        var readable = ConsoleReadRules.SanitizeDifference(audit.DifferenceJson);
        Assert.Equal(new[] { "dbip", "ipinfo" }, readable["before"].GetProperty("providerIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "ipinfo", "dbip" }, readable["after"].GetProperty("providerIds").EnumerateArray().Select(item => item.GetString()));
    }

    private static string[] Ids(IRuntimeProviderRegistry registry) => registry.Providers.Select(source => source.Descriptor.Id).ToArray();
    private static RuntimeProviderRegistry CreateRegistry(IServiceProvider services, params IIpDataProvider[] providers) => new(providers, services,
        new ProviderFileFactory(OptionsFactory.Create(new GeoLite2Options()), OptionsFactory.Create(new Ip2LocationOptions()),
            OptionsFactory.Create(new DbIpOptions()), OptionsFactory.Create(new Ip2RegionOptions()), OptionsFactory.Create(new IpinfoOptions()),
            TimeProvider.System, OptionsFactory.Create(new ChunzhenOptions())));
    private sealed class FixedIds : IIdService { public long NewId() => 100; }
    private sealed class FixedAddress : IClientIpService { public IPAddress? GetClientIp(HttpContext context) => IPAddress.Loopback; }
    private sealed class OrderedProvider(string id, bool delayFirst = false) : IIpDataProvider
    {
        public ProviderDescriptor Descriptor => new(id, id, "ready", true, true, true, false, true, "controlled", "ready", "", "");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            if (Interlocked.Increment(ref _calls) == 1 && delayFirst) await Continue.Task.WaitAsync(cancellationToken);
            return new("success", new IpFields { Region = id }, "controlled", "controlled");
        }
    }
}
