using IpLocation.Api.Models.Providers;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers.Chunzhen;
using IpLocation.Api.Service.impl.Providers;
using IpLocation.Api.Service.impl.Providers.DbIp;
using IpLocation.Api.Service.impl.Providers.GeoLite2;
using IpLocation.Api.Service.impl.Providers.Ip2Location;
using IpLocation.Api.Service.impl.Providers.Ip2Region;
using IpLocation.Api.Service.impl.Providers.Ipinfo;
using IpLocation.Api.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IpLocation.Api.Common;

public static class ProviderRegistration
{
    public static IServiceCollection AddIpDataProviders(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddOptions<GeoLite2Options>().BindConfiguration("GeoLite2");
        services.AddOptions<Ip2LocationOptions>().BindConfiguration("IP2Location");
        services.AddOptions<DbIpOptions>().BindConfiguration("DbIp");
        services.AddOptions<Ip2RegionOptions>().BindConfiguration("Ip2Region");
        services.AddOptions<IpinfoOptions>().BindConfiguration("Ipinfo");
        services.AddOptions<ChunzhenOptions>().BindConfiguration("Chunzhen");
        services.AddOptions<DatabaseUploadOptions>().BindConfiguration("Uploads")
            .Validate(value => value.MaxFileBytes is > 0 and <= 1_073_741_824 && value.MaxTotalBytes >= value.MaxFileBytes &&
                value.MaxTotalBytes <= 4_294_967_296 && Path.IsPathRooted(value.StorageRoot), "上传资源限额或存储目录无效。")
            .ValidateOnStart();
        services.AddSingleton<ProviderFileFactory>();
        services.AddSingleton<IRuntimeProviderRegistry, RuntimeProviderRegistry>();
        services.AddSingleton<DatabaseManagementService>();
        services.AddHostedService<RuntimeProviderRefreshWorker>();
        services.AddSingleton<IIpDataProvider, DbIpCityDataProvider>();
        services.AddSingleton<IIpDataProvider, IpinfoLiteDataProvider>();
        services.AddSingleton<IIpDataProvider, Ip2RegionDataProvider>();
        services.AddSingleton<IIpDataProvider, ChunzhenDataProvider>();
        services.AddSingleton<IIpDataProvider, GeoLite2CityDataProvider>();
        services.AddSingleton<IIpDataProvider, Ip2LocationMmdbDataProvider>();
        return services;

    }
}
