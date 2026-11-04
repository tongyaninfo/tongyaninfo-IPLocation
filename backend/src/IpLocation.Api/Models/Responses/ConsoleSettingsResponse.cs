using IpLocation.Api.Models.Providers;
using IpLocation.Api.Options;

namespace IpLocation.Api.Models.Responses;

public sealed record ConsoleSettingsRuntime(bool DatabaseEnabled, bool RetainFullSourceIp,
    int? RecentEventRetentionDays, int QueueCapacity, int SessionMinutes, int TrustedProxyCount, int ForwardLimit,
    bool QueueCapacityRequiresRestart = false);

public sealed record ConsoleSettingsResponse(long Revision, QueryOptions Query, string[] AllowedNetworks,
    ConsoleSettingsRuntime Runtime, IReadOnlyList<ProviderDescriptor> Providers, string? CurrentSourceAddress = null,
    ConsoleBasicSettings? Basic = null);
