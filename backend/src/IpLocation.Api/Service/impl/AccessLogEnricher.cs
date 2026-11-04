using IpLocation.Api.Common;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>消费者串行查询固定CZ88来源；批内有界复用，异常/超时只改变定位状态，不丢弃访问事件。</summary>
public sealed class AccessLogEnricher(IOptions<AccessLocationOptions> options, IRuntimeProviderRegistry? registry = null,
    IConsoleRuntimeSettings? runtime = null) : IAccessLogEnricher
{
    private Task<ProviderQueryResult>? _pendingLookup;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IReadOnlyList<AccessEvent>> EnrichAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || events.Count == 0 || runtime?.Current.Basic?.AccessLogRetentionDays == 0) return events;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.Value.BatchTimeoutMilliseconds);
            var result = new AccessEvent[events.Count];
            var cache = new Dictionary<(string Address, string? Version), AccessSourceLocation>();
            var provider = registry?.Providers.FirstOrDefault(item => ProviderCodes.Public(item.Descriptor.Id) == ProviderCodes.Cz88);
            for (var index = 0; index < events.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (runtime?.Current.Basic?.AccessLogRetentionDays == 0) return events;
                var item = events[index];
                if (item.SourceLocationJson is not null) { result[index] = item; continue; }
                AccessSourceLocation location;
                var source = item.SourceAddress;
                if (source?.Contains('/') == true) location = Status("unsupported");
                else if (source is null || !IpAddressParser.TryParse(source, out var address)) location = Status("invalid");
                else if (IpAddressParser.Classify(address) != "public") location = Status("special");
                else if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) location = Status("unsupported");
                else if (provider is null) location = Status("unavailable");
                else
                {
                    var descriptor = provider.Descriptor;
                    if (!descriptor.Enabled) location = Status("disabled", descriptor.Version);
                    else if (!descriptor.Ready) location = Status(descriptor.Status == "unavailable" ? "unavailable" : "notReady", descriptor.Version);
                    else if (deadline.IsCancellationRequested) location = Status("timeout", descriptor.Version);
                    else if (cache.TryGetValue((address.ToString(), descriptor.Version), out var cached)) location = cached;
                    else
                    {
                        location = await LookupAsync(provider, address, deadline.Token, cancellationToken);
                        cache[(address.ToString(), descriptor.Version)] = location;
                    }
                }
                result[index] = item with { SourceLocationJson = AccessSourceLocations.Write(location) };
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<AccessSourceLocation> LookupAsync(IIpDataProvider provider, System.Net.IPAddress address,
        CancellationToken batchToken, CancellationToken stopToken)
    {
        var version = provider.Descriptor.Version;
        // 即使一个未来适配器忽略取消，也不继续为后续事件/批次累积未完成查询。
        if (_pendingLookup is { IsCompleted: false }) return Status("timeout", version);
        if (_pendingLookup?.IsFaulted == true) _ = _pendingLookup.Exception;
        _pendingLookup = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
        timeout.CancelAfter(options.Value.LookupTimeoutMilliseconds);
        try
        {
            _pendingLookup = provider.QueryAsync(address, timeout.Token);
            _ = _pendingLookup.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var response = await _pendingLookup.WaitAsync(timeout.Token);
            version = response.Version ?? version;
            return response.Status == "success" ? AccessSourceLocations.FromResult(version, response.Fields.Region, response.Fields.Isp)
                : Status(response.Status is "notFound" or "unsupported" or "timeout" ? response.Status : "unavailable", version);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Status("timeout", version); }
        catch (Exception) { return Status("unavailable", version); }
        finally { if (_pendingLookup?.IsCompleted == true) _pendingLookup = null; }
    }

    internal static AccessSourceLocation Status(string status, string? version = null) =>
        new(ProviderCodes.Cz88, version, status, null, null, null, null, null);
}
