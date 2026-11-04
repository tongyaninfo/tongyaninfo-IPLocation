using IpLocation.Api.Options;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Models.Requests;

public sealed record ConsoleSettingsRequest(long Revision, QueryOptions Query, string[] AllowedNetworks,
    ConsoleBasicSettings? Basic = null);
