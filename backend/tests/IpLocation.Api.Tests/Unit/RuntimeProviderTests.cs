using System.Net;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class RuntimeProviderTests
{
    [Fact]
    public async Task ReaderRetirementWaitsForInFlightLookup()
    {
        var baseline = new ControlledProvider("geolite2");
        using var runtime = new RuntimeProviderRegistry.RuntimeProvider(baseline, false);
        var first = new ControlledProvider("geolite2", block: true);
        runtime.Replace(first, "first");
        var query = runtime.QueryAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None);
        await first.Entered.Task;
        var second = new ControlledProvider("geolite2");
        runtime.Replace(second, "second");
        Assert.False(first.Disposed);
        first.Continue.TrySetResult();
        Assert.Equal("success", (await query).Status);
        Assert.True(first.Disposed);
        Assert.False(second.Disposed);
        Assert.False(baseline.Disposed); // 配置基线的句柄归 DI 所有，注册表不重复释放。
    }

    [Fact]
    public async Task DisabledSourceSkipsUnderlyingReaderAndRejectsStalePublishedState()
    {
        var source = new ControlledProvider("geolite2");
        using var runtime = new RuntimeProviderRegistry.RuntimeProvider(source, false);
        runtime.SetState(false, 3);
        runtime.SetState(true, 2);
        Assert.Equal("disabled", runtime.Descriptor.Status);
        Assert.Equal("disabled", (await runtime.QueryAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None)).Status);
        Assert.Equal(0, source.Calls);
        runtime.SetState(true, 4);
        Assert.Equal("success", (await runtime.QueryAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None)).Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task PersistentStateRecoveryFailsClosedUntilLoaded()
    {
        using var runtime = new RuntimeProviderRegistry.RuntimeProvider(new ControlledProvider("geolite2"), true);
        Assert.False(runtime.Descriptor.Ready);
        Assert.Equal("unavailable", (await runtime.QueryAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None)).Status);
        runtime.SetState(true, 0);
        Assert.True(runtime.Descriptor.Ready);
    }

    [Fact]
    public async Task UnconfiguredSourceCanBeEnabledWithoutPretendingReady()
    {
        var baseline = new UnconfiguredIpDataProvider(new ProviderDescriptor("chunzhen", "纯真社区版", "notReady",
            true, true, false, false, false, null, "尚未配置纯真数据文件/读取器。", "https://example.com/license", "test"));
        using var runtime = new RuntimeProviderRegistry.RuntimeProvider(baseline, false);
        runtime.SetState(true, 1);
        Assert.Equal("notReady", runtime.Descriptor.Status);
        Assert.True(runtime.Descriptor.Enabled);
        Assert.False(runtime.Descriptor.Ready);
        var query = new IpQueryService([runtime], OptionsFactory.Create(new QueryOptions()));
        var response = await query.QueryAsync("8.8.8.8", "chunzhen", CancellationToken.None);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("DB_NOT_READY", response.Result.Code);
        Assert.Equal("CZ88", response.Result.Data!.Sources.Single().Id);
    }

    [Fact]
    public async Task ReadySourceQueriesWithoutHistoricalLicenseConfirmationOrReviewMode()
    {
        var source = new ControlledProvider("dbip", licenseConfirmed: false);
        Assert.False(source.Descriptor.LicenseConfirmed);
        Assert.Null(source.Descriptor.LicenseReviewMode);
        var query = new IpQueryService([source], OptionsFactory.Create(new QueryOptions()));
        var response = await query.QueryAsync("8.8.8.8", "dbip", CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("success", response.Result.Data!.Sources.Single().Status);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task TechnicallyReadyManualUploadCanQueryWithoutClaimingLicenseVerification()
    {
        var source = new ControlledProvider("ipinfo");
        using var manual = new ProviderFileFactory.ManualReviewProvider(source);
        using var runtime = new RuntimeProviderRegistry.RuntimeProvider(manual, false);
        Assert.Equal("manual", runtime.Descriptor.LicenseReviewMode);
        Assert.False(runtime.Descriptor.LicenseConfirmed);
        Assert.True(runtime.Descriptor.Ready);
        var query = new IpQueryService([runtime], OptionsFactory.Create(new QueryOptions()));
        var response = await query.QueryAsync("8.8.8.8", "ipinfo", CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("success", response.Result.Data!.Sources.Single().Status);
        Assert.Equal(1, source.Calls);
        runtime.SetState(false, 1);
        var disabled = await query.QueryAsync("8.8.8.8", "ipinfo", CancellationToken.None);
        Assert.Equal("DB_DISABLED", disabled.Result.Code);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task QueryUsesPublishedDefaultWithoutRebuildingService()
    {
        var settings = new ControlledSettings();
        var query = new IpQueryService([new ControlledProvider("geolite2"), new ControlledProvider("dbip")],
            OptionsFactory.Create(new QueryOptions { DefaultDatabase = "geolite2" }), runtimeSettings: settings);
        Assert.Equal("geolite2", (await query.QueryAsync("8.8.8.8", null, CancellationToken.None)).Result.Data!.Sources.Single().Id);
        settings.Publish(settings.Current with { Revision = 2, Query = new QueryOptions { DefaultDatabase = "dbip" } });
        Assert.Equal("dbip", (await query.QueryAsync("8.8.8.8", null, CancellationToken.None)).Result.Data!.Sources.Single().Id);
        Assert.Equal("geolite2", (await query.QueryAsync("8.8.8.8", "geolite2", CancellationToken.None)).Result.Data!.Sources.Single().Id);
    }

    private sealed class ControlledSettings : IConsoleRuntimeSettings
    {
        public ConsoleRuntimeSnapshot Current { get; private set; } = new(1, new QueryOptions { DefaultDatabase = "geolite2" }, [], true);
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Publish(ConsoleRuntimeSnapshot snapshot) => Current = snapshot;
    }
    private sealed class ControlledProvider(string id, bool block = false, bool licenseConfirmed = true) : IIpDataProvider, IDisposable
    {
        public ProviderDescriptor Descriptor => new(id, id, "ready", true, true, true, licenseConfirmed, true, id + "-version", "ready", "https://example.com/license", "test");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public async Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Calls++; Entered.TrySetResult();
            if (block) await Continue.Task.WaitAsync(cancellationToken);
            Assert.False(Disposed);
            return new("success", new IpFields { Country = "controlled" }, "ready", id + "-version");
        }
        public void Dispose() => Disposed = true;
    }
}
