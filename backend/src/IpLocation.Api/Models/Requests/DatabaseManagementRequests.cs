using System.ComponentModel.DataAnnotations;

namespace IpLocation.Api.Models.Requests;

public sealed class DatabaseStateRequest
{
    public bool Enabled { get; set; }
    [Range(0, long.MaxValue)] public long Revision { get; set; }
    public string? DefaultDatabase { get; set; }
}

public sealed class DatabaseActivationRequest
{
    [Required] public string VersionId { get; set; } = "";
    [Range(0, long.MaxValue)] public long Revision { get; set; }
}

public sealed class DatabaseOrderRequest
{
    [Required] public string[] ProviderIds { get; set; } = [];
    [Range(0, long.MaxValue)] public long Revision { get; set; }
}
