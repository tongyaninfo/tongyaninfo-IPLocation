using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>编排只查询已启用且技术就绪的来源；跳过的来源不把成功聚合降为部分失败。</summary>
public sealed class IpQueryService(IEnumerable<IIpDataProvider> providers, IOptions<QueryOptions> options,
    IRuntimeProviderRegistry? registry = null, IConsoleRuntimeSettings? runtimeSettings = null) : IIpQueryService
{
    private readonly IReadOnlyList<IIpDataProvider> _baselineProviders = providers.ToArray();

    public IReadOnlyList<ProviderDescriptor> GetProviders() => (registry?.Providers ?? _baselineProviders)
        .Select(provider => provider.Descriptor with { Id = ProviderCodes.Public(provider.Descriptor.Id) }).ToArray();

    public async Task<QueryResponse> QueryAsync(string ip, string? database, CancellationToken cancellationToken)
    {
        var currentOptions = runtimeSettings?.Current.Query ?? options.Value;
        var providerSnapshot = registry?.Providers ?? _baselineProviders;
        if (!IpAddressParser.TryParse(ip, out var address))
        {
            return new(400, ApiResult<QueryResult>.Fail("INVALID_IP", "请输入一个合法的 IPv4 或 IPv6 地址。"));
        }

        var selectedId = ProviderCodes.Public(database ?? currentOptions.DefaultDatabase);
        var all = string.Equals(selectedId, "all", StringComparison.Ordinal);
        var selected = all ? providerSnapshot : providerSnapshot.Where(provider => ProviderCodes.Public(provider.Descriptor.Id) == selectedId).ToArray();
        if (selected.Count == 0)
        {
            return new(400, ApiResult<QueryResult>.Fail("UNKNOWN_DATABASE", "指定的数据来源未登记。"));
        }

        var addressType = IpAddressParser.Classify(address);
        if (addressType != "public")
        {
            var skipped = selected.Select(provider => Metadata(provider.Descriptor, "unsupported", "特殊用途地址不执行公网定位。"));
            return new(200, ApiResult<QueryResult>.Ok(new(address.ToString(), addressType, "unsupported", skipped.ToArray()),
                "已识别特殊用途地址，未调用公网数据源。"));
        }

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(currentOptions.TotalTimeoutMs);
        using var concurrency = new SemaphoreSlim(currentOptions.MaxConcurrentSources);
        var eligible = selected.Where(provider => BlockedState(provider.Descriptor) is null).ToArray();
        var eligibleSet = eligible.ToHashSet();
        var tasks = selected.Select(async provider => eligibleSet.Contains(provider)
            ? await QuerySourceAsync(provider, address, concurrency, overall.Token, cancellationToken, currentOptions.ProviderTimeoutMs)
            : Metadata(provider.Descriptor, BlockedState(provider.Descriptor)!, provider.Descriptor.Message));
        var sources = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        if (eligible.Length == 0)
        {
            var code = all ? "DB_NOT_READY" : StateCode(sources[0].Status);
            return new(503, ApiResult<QueryResult>.Fail(code, "没有可查询的数据来源，未自动换库。",
                new(address.ToString(), addressType, "unavailable", sources)));
        }

        var queried = sources.Where(source => eligible.Any(provider => ProviderCodes.Public(provider.Descriptor.Id) == source.Id)).ToArray();
        var useful = queried.Any(source => source.Status is "success" or "notFound");
        var failed = queried.Any(source => source.Status is not ("success" or "notFound" or "unsupported"));
        if (queried.All(source => source.Status == "unsupported"))
        {
            return new(200, ApiResult<QueryResult>.Ok(new(address.ToString(), addressType, "unsupported", sources),
                "已就绪来源均不支持该地址族。"));
        }

        if (!useful)
        {
            return new(503, ApiResult<QueryResult>.Fail("DB_UNAVAILABLE", "所有可查询来源均未能完成查询。",
                new(address.ToString(), addressType, "unavailable", sources)));
        }

        var status = failed ? "partial" : queried.Any(source => source.Status == "success") ? "success" : "notFound";
        return new(200, ApiResult<QueryResult>.Ok(new(address.ToString(), addressType, status, sources),
            status == "partial" ? "部分来源暂不可用，已返回其他结果。" : status == "notFound" ? "已查询，未找到匹配记录。" : "查询完成"));
    }

    private async Task<SourceResult> QuerySourceAsync(IIpDataProvider provider, IPAddress address,
        SemaphoreSlim concurrency, CancellationToken overallToken, CancellationToken requestToken, int providerTimeoutMs)
    {
        var descriptor = provider.Descriptor with { Id = ProviderCodes.Public(provider.Descriptor.Id) };
        if ((address.AddressFamily == AddressFamily.InterNetwork && !descriptor.SupportsIpv4) ||
            (address.AddressFamily == AddressFamily.InterNetworkV6 && !descriptor.SupportsIpv6))
        {
            return Metadata(descriptor, "unsupported", "该来源不支持此地址族。");
        }

        var clock = Stopwatch.StartNew();
        var acquired = false;
        try
        {
            await concurrency.WaitAsync(overallToken);
            acquired = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
            timeout.CancelAfter(providerTimeoutMs);
            // WaitAsync 保证编排截止时间；适配器仍必须传递取消信号以释放底层资源。
            var result = await provider.QueryAsync(address, timeout.Token).WaitAsync(timeout.Token);
            var status = result.Status is "success" or "notFound" or "unsupported" or "timeout" or "unavailable"
                ? result.Status : "unavailable";
            return new(descriptor.Id, descriptor.Name, status, result.Version ?? descriptor.Version,
                status == "success" ? result.Fields : new IpFields(), clock.ElapsedMilliseconds, result.Message,
                descriptor.LicenseUrl, descriptor.Attribution);
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            return Metadata(descriptor, "timeout", "来源查询超时。", clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 不将供应商异常文本、服务器路径或认证信息放入公开响应。
            return Metadata(descriptor, "unavailable", "来源暂不可用。", clock.ElapsedMilliseconds);
        }
        finally
        {
            if (acquired) concurrency.Release();
        }
    }

    private static string? BlockedState(ProviderDescriptor descriptor)
    {
        if (!descriptor.Enabled) return "disabled";
        if (!descriptor.Ready) return descriptor.Status == "unavailable" ? "unavailable" : "notReady";
        return null;
    }

    private static string StateCode(string state) => state switch
    {
        "disabled" => "DB_DISABLED",
        "unavailable" => "DB_UNAVAILABLE",
        _ => "DB_NOT_READY"
    };

    private static SourceResult Metadata(ProviderDescriptor descriptor, string status, string message, long elapsed = 0) =>
        new(ProviderCodes.Public(descriptor.Id), descriptor.Name, status, descriptor.Version, new IpFields(), elapsed, message,
            descriptor.LicenseUrl, descriptor.Attribution);
}
