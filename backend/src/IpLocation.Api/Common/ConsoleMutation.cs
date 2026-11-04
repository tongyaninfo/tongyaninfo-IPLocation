using System.Globalization;
using System.Text.Json;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Service;
using Microsoft.EntityFrameworkCore;

namespace IpLocation.Api.Common;

/// <summary>管理写操作使用相同事务锁顺序；身份撤销和业务审计不能被并发写绕过。</summary>
public static class ConsoleMutation
{
    public static async Task<bool> LockActorAsync(IpLocationDbContext db, AdminAccount actor, CancellationToken cancellationToken,
        HttpContext? context = null)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4933277946142572)", cancellationToken);
        var rows = await db.AdminAccounts.FromSqlInterpolated(
            $"SELECT * FROM iplocation.\"ADMIN_ACCOUNT\" WHERE \"ID\" = {actor.Id} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken);
        var current = rows.SingleOrDefault();
        if (current is not { Enabled: true } || current.SecurityVersion != actor.SecurityVersion) return false;
        if (context is not null)
        {
            var address = context.RequestServices.GetRequiredService<IClientIpService>().GetClientIp(context);
            var marker = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(
                row => row.Key == "console.allowlistManaged", cancellationToken);
            if (marker is not null && JsonSerializer.Deserialize<bool>(marker.ValueJson))
            {
                var networks = await db.AdminIpAllowlists.AsNoTracking().Where(row => row.Enabled)
                    .Select(row => row.Network).ToListAsync(cancellationToken);
                if (address is null || !networks.Any(network => network.Contains(address))) return false;
            }
            else if (!context.RequestServices.GetRequiredService<IConsoleAccessService>().IsAllowed(address)) return false;
        }
        return true;
    }

    public static void AddAudit(IpLocationDbContext db, IIdService ids, IClientIpService clientIp, HttpContext context,
        string action, string targetType, string? targetId, object difference, string result = "success")
    {
        var actor = (AdminAccount)context.Items[ConsoleAuthentication.AccountKey]!;
        db.AdminAuditEvents.Add(new AdminAuditEvent
        {
            Id = ids.NewId(), ActorId = actor.Id, SourceAddress = clientIp.GetClientIp(context), Action = action,
            TargetType = targetType, TargetId = targetId, DifferenceJson = JsonSerializer.Serialize(difference,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Result = result,
            TraceId = context.TraceIdentifier, OccurredAtUtc = DateTimeOffset.UtcNow
        });
    }
}
