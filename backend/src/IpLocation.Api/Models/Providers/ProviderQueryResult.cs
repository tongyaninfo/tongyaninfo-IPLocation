using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Models.Providers;

/// <summary>适配器只返回自身查询结果，不接触 HttpContext 或编排其他来源。</summary>
public sealed record ProviderQueryResult(string Status, IpFields Fields, string Message, string? Version = null);
