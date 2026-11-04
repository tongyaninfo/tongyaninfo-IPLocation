using IpLocation.Api.Options;
using IpLocation.Api.Common;
using IpLocation.Api.Service.impl.Providers.Chunzhen;
using IpLocation.Api.Service.impl.Providers.DbIp;
using IpLocation.Api.Service.impl.Providers.GeoLite2;
using IpLocation.Api.Service.impl.Providers.Ip2Location;
using IpLocation.Api.Service.impl.Providers.Ip2Region;
using IpLocation.Api.Service.impl.Providers.Ipinfo;
using Microsoft.Extensions.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>复用既有来源适配器打开新快照，不修改原始运行配置或原文件。</summary>
public sealed class ProviderFileFactory(IOptions<GeoLite2Options> geo, IOptions<Ip2LocationOptions> ip2,
    IOptions<DbIpOptions> dbip, IOptions<Ip2RegionOptions> region, IOptions<IpinfoOptions> info, TimeProvider clock,
    IOptions<ChunzhenOptions> chunzhen)
{
    public bool Supports(string id) => ProviderCodes.Public(id) is "geolite2" or "ip2location" or "dbip" or "ipinfo" or "ip2region" or ProviderCodes.Cz88;
    public IIpDataProvider Open(string id, string? ipv4 = null, string? ipv6 = null, bool uploaded = false)
    {
        id = ProviderCodes.Public(id);
        if (id != "ip2region" && ipv4 is not null && ipv6 is not null && ipv4 != ipv6)
            return new SplitFamilyProvider(Open(id, ipv4, null, uploaded), Open(id, null, ipv6, uploaded));
        IIpDataProvider provider = id switch
    {
        ProviderCodes.Cz88 => new ChunzhenDataProvider(OptionsFactory.Create(new ChunzhenOptions { Enabled = true,
            DatabasePath = ipv4 ?? chunzhen.Value.DatabasePath, MaxDatabaseBytes = chunzhen.Value.MaxDatabaseBytes,
            MaxIndexEntries = chunzhen.Value.MaxIndexEntries })),
        "geolite2" => new GeoLite2CityDataProvider(OptionsFactory.Create(new GeoLite2Options { Enabled = true,
            LicenseConfirmed = uploaded ? false : geo.Value.LicenseConfirmed, DatabasePath = ipv4 ?? ipv6 ?? geo.Value.DatabasePath,
            MaxAgeDays = geo.Value.MaxAgeDays }), clock),
        "ip2location" => new Ip2LocationMmdbDataProvider(OptionsFactory.Create(new Ip2LocationOptions { Enabled = true,
            LicenseConfirmed = uploaded ? false : ip2.Value.LicenseConfirmed, DatabasePath = ipv4 ?? ip2.Value.DatabasePath })),
        "dbip" => new DbIpCityDataProvider(OptionsFactory.Create(new DbIpOptions { Enabled = true,
            LicenseConfirmed = uploaded ? false : dbip.Value.LicenseConfirmed, DatabasePath = ipv4 ?? ipv6 ?? dbip.Value.DatabasePath })),
        "ipinfo" => new IpinfoLiteDataProvider(OptionsFactory.Create(new IpinfoOptions { Enabled = true,
            LicenseConfirmed = uploaded ? false : info.Value.LicenseConfirmed, DatabasePath = ipv4 ?? ipv6 ?? info.Value.DatabasePath })),
        "ip2region" => new Ip2RegionDataProvider(OptionsFactory.Create(new Ip2RegionOptions { Enabled = true,
            LicenseConfirmed = uploaded ? false : region.Value.LicenseConfirmed,
            Ipv4DatabasePath = ipv4 ?? region.Value.Ipv4DatabasePath,
            Ipv6DatabasePath = ipv6 ?? region.Value.Ipv6DatabasePath,
            MaxDatabaseBytes = uploaded ? 1_073_741_824 : region.Value.MaxDatabaseBytes })),
        _ => throw new ArgumentException("Unsupported provider.", nameof(id))
    };
        // 兼容历史字段仅作来源说明，工厂不将其作为构造、校验或查询资格条件。
        return uploaded ? new ManualReviewProvider(provider) : provider;
    }

    public IIpDataProvider Validate(string id, string path, string family) => id == "ip2region"
        ? new ManualReviewProvider(new Ip2RegionDataProvider(OptionsFactory.Create(new Ip2RegionOptions { Enabled = true, LicenseConfirmed = false,
            Ipv4DatabasePath = family == "ipv4" ? path : "", Ipv6DatabasePath = family == "ipv6" ? path : "",
            MaxDatabaseBytes = Math.Max(region.Value.MaxDatabaseBytes, new FileInfo(path).Length) })))
        : Open(id, family == "ipv6" ? null : path, family == "ipv6" ? path : null, true);

    internal sealed class ManualReviewProvider(IIpDataProvider provider) : IIpDataProvider, IDisposable
    {
        public Models.Providers.ProviderDescriptor Descriptor => provider.Descriptor with
            { LicenseReviewMode = "manual", LicenseConfirmed = false };
        public Task<Models.Providers.ProviderQueryResult> QueryAsync(System.Net.IPAddress address, CancellationToken cancellationToken) =>
            provider.QueryAsync(address, cancellationToken);
        public void Dispose() => (provider as IDisposable)?.Dispose();
    }

    private sealed class SplitFamilyProvider(IIpDataProvider v4, IIpDataProvider v6) : IIpDataProvider, IDisposable
    {
        public Models.Providers.ProviderDescriptor Descriptor
        {
            get
            {
                var a = v4.Descriptor; var b = v6.Descriptor;
                return a with { Ready = a.Ready && b.Ready, Status = a.Ready && b.Ready ? "ready" : "unavailable",
                    SupportsIpv4 = a.SupportsIpv4, SupportsIpv6 = b.SupportsIpv6,
                    Version = $"v4={a.Version};v6={b.Version}", LicenseConfirmed = a.LicenseConfirmed && b.LicenseConfirmed };
            }
        }
        public Task<Models.Providers.ProviderQueryResult> QueryAsync(System.Net.IPAddress address, CancellationToken cancellationToken) =>
            (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? v4 : v6).QueryAsync(address, cancellationToken);
        public void Dispose() { (v4 as IDisposable)?.Dispose(); (v6 as IDisposable)?.Dispose(); }
    }
}
