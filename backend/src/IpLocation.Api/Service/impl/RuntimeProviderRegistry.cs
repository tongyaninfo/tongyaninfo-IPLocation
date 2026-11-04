using System.Net;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Providers;
using Microsoft.EntityFrameworkCore;

namespace IpLocation.Api.Service.impl;

/// <summary>查询只读取内存快照。持久指针重载与句柄替换串行，旧句柄在最后一个查询结束后释放。</summary>
public sealed class RuntimeProviderRegistry : IRuntimeProviderRegistry, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ProviderFileFactory _files;
    private readonly Dictionary<string, RuntimeProvider> _providers;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private ProviderOrderSnapshot _order;
    public ProviderOrderSnapshot Order => Volatile.Read(ref _order);
    public IReadOnlyList<IIpDataProvider> Providers => Order.Providers;

    public RuntimeProviderRegistry(IEnumerable<IIpDataProvider> providers, IServiceProvider services, ProviderFileFactory files)
    {
        _services = services;
        _files = files;
        var needsLoad = services.GetService<IDbContextFactory<IpLocationDbContext>>() is not null;
        _providers = providers.ToDictionary(provider => ProviderCodes.Public(provider.Descriptor.Id), provider => new RuntimeProvider(provider, needsLoad));
        _order = new(0, Array.AsReadOnly(_providers.Values.Cast<IIpDataProvider>().ToArray()));
    }

    public long Revision(string id) => _providers.TryGetValue(ProviderCodes.Public(id), out var provider) ? provider.Revision : 0;
    public void PublishState(string id, bool enabled, long revision) => _providers[ProviderCodes.Public(id)].SetState(enabled, revision);
    public void PublishOrder(IReadOnlyList<string> providerIds, long revision)
    {
        var ids = NormalizeOrder(providerIds, _providers.Keys);
        var replacement = new ProviderOrderSnapshot(revision, Array.AsReadOnly(ids.Select(id => (IIpDataProvider)_providers[id]).ToArray()));
        while (true)
        {
            var current = Order;
            if (revision < current.Revision) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _order, replacement, current), current)) return;
        }
    }

    internal static string[] NormalizeOrder(IEnumerable<string>? saved, IEnumerable<string> registered)
    {
        var known = registered.Select(ProviderCodes.Public).Distinct(StringComparer.Ordinal).ToArray();
        var remaining = known.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>(known.Length);
        // 恢复旧设置时过滤已移除/重复ID，新登记来源按注册顺序补到末尾。
        foreach (var oldId in saved ?? [])
            if (oldId is not null) { var id = ProviderCodes.Public(oldId); if (remaining.Remove(id)) result.Add(id); }
        result.AddRange(known.Where(remaining.Contains));
        return result.ToArray();
    }

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var factory = _services.GetService<IDbContextFactory<IpLocationDbContext>>();
        if (factory is null) return;
        await _reload.WaitAsync(cancellationToken);
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
            var settings = await db.SystemSettings.AsNoTracking().Where(setting => setting.Key.StartsWith("provider."))
                .ToDictionaryAsync(setting => setting.Key, cancellationToken);
            var active = await (from activation in db.IpDatabaseActivations.AsNoTracking()
                join version in db.IpDatabaseVersions.AsNoTracking() on activation.VersionId equals version.Id
                select new { activation.ProviderCode, activation.AddressFamily, activation.VersionId, activation.Revision,
                    version.StoragePath, version.Status }).ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (var (id, current) in _providers)
            {
                settings.TryGetValue(ProviderCodes.EnabledSettingKey(id), out var setting);
                var enabled = setting is null ? current.BaselineEnabled : JsonSerializer.Deserialize<bool>(setting.ValueJson);
                var storageId = ProviderCodes.Storage(id);
                var pointers = active.Where(item => item.ProviderCode == storageId).OrderBy(item => item.AddressFamily).ToArray();
                var fingerprint = string.Join(";", pointers.Select(item => $"{item.AddressFamily}:{item.VersionId}:{item.Revision}"));
                if (current.Fingerprint != fingerprint || (!current.ReaderReady && enabled && _files.Supports(id)))
                {
                    IIpDataProvider? replacement = null;
                    if (_files.Supports(id))
                    {
                        var v4 = pointers.SingleOrDefault(item => item.AddressFamily == "ipv4");
                        var v6 = pointers.SingleOrDefault(item => item.AddressFamily == "ipv6");
                        replacement = _files.Open(id, v4?.StoragePath, v6?.StoragePath, pointers.Length > 0 && pointers.All(item => item.Status == "ready"));
                        if (pointers.Any(item => item.Status != "ready"))
                        {
                            (replacement as IDisposable)?.Dispose();
                            replacement = null;
                            enabled = false;
                        }
                    }
                    if (replacement is not null) current.Replace(replacement, fingerprint);
                }
                current.SetState(enabled, setting?.Revision ?? 0);
            }
            settings.TryGetValue("provider.displayOrder", out var order);
            PublishOrder(order is null ? [] : JsonSerializer.Deserialize<string[]>(order.ValueJson) ?? [], order?.Revision ?? 0);
        }
        finally { _reload.Release(); }
    }

    public void Dispose() { foreach (var provider in _providers.Values) provider.Dispose(); }

    internal sealed class RuntimeProvider : IIpDataProvider, IDisposable
    {
        private readonly object _sync = new();
        private Handle _current;
        private bool _enabled;
        private bool _loaded;
        public bool BaselineEnabled { get; }
        public long Revision { get; private set; }
        public string? Fingerprint { get; private set; } = "";
        public bool ReaderReady { get { lock (_sync) return _current.Provider.Descriptor.Ready; } }

        public RuntimeProvider(IIpDataProvider baseline, bool needsLoad)
        {
            _current = new(baseline, false); BaselineEnabled = _enabled = baseline.Descriptor.Enabled; _loaded = !needsLoad;
        }
        public ProviderDescriptor Descriptor
        {
            get
            {
                lock (_sync)
                {
                    var descriptor = _current.Provider.Descriptor with { Id = ProviderCodes.Public(_current.Provider.Descriptor.Id) };
                    if (!_loaded) return descriptor with { Ready = false, Status = "unavailable", Message = "持久化来源状态正在恢复，尚未开放查询。" };
                    // 旧状态只兼容为技术状态，不让历史策略标记成为当前查询的资格判断。
                    if (descriptor.Status is not ("ready" or "notReady" or "disabled" or "unavailable"))
                        descriptor = descriptor with { Status = descriptor.Ready ? "ready" : "notReady", Message = descriptor.Ready ? descriptor.Message : "数据文件或读取器尚未就绪。" };
                    return !_enabled ? descriptor with { Enabled = false, Ready = false, Status = "disabled", Message = "此来源已在后台停用。" }
                        : descriptor with { Enabled = true };
                }
            }
        }
        public void SetState(bool enabled, long revision) { lock (_sync) { if (revision < Revision) return; _enabled = enabled; _loaded = true; Revision = revision; } }
        public void Replace(IIpDataProvider provider, string fingerprint)
        {
            lock (_sync)
            {
                var old = _current; _current = new(provider, true); Fingerprint = fingerprint;
                old.Retired = true; ReleaseUnused(old);
            }
        }
        public async Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
        {
            Handle selected;
            lock (_sync)
            {
                var descriptor = Descriptor;
                if (!descriptor.Ready) return new(descriptor.Status, new(), descriptor.Message, descriptor.Version);
                selected = _current; selected.Readers++;
            }
            try { return await selected.Provider.QueryAsync(address, cancellationToken); }
            finally { lock (_sync) { selected.Readers--; ReleaseUnused(selected); } }
        }
        private static void ReleaseUnused(Handle handle)
        {
            if (handle.Retired && handle.Readers == 0 && handle.Owned && !handle.Disposed)
            { handle.Disposed = true; (handle.Provider as IDisposable)?.Dispose(); }
        }
        public void Dispose() { lock (_sync) { _current.Retired = true; ReleaseUnused(_current); } }
        private sealed class Handle(IIpDataProvider provider, bool owned)
        {
            public IIpDataProvider Provider { get; } = provider;
            public bool Owned { get; } = owned;
            public int Readers;
            public bool Retired;
            public bool Disposed;
        }
    }
}
