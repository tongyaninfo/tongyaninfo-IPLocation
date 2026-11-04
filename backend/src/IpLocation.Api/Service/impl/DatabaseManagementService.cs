using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace IpLocation.Api.Service.impl;

/// <summary>来源变更与可靠审计同事务；文件采用不可变存储，失败文件保留且计入占用，不自动删除。</summary>
public sealed class DatabaseManagementService(IServiceProvider services, IRuntimeProviderRegistry registry,
    ProviderFileFactory files, IConsoleRuntimeSettings settings, IOptions<DatabaseUploadOptions> uploadOptions,
    IIdService ids, IClientIpService clientIp, IWebHostEnvironment environment, ILogger<DatabaseManagementService> logger)
{
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private IDbContextFactory<IpLocationDbContext> Factory => services.GetRequiredService<IDbContextFactory<IpLocationDbContext>>();

    public async Task<object> GetAsync(CancellationToken cancellationToken)
    {
        await registry.ReloadAsync(cancellationToken);
        await settings.ReloadAsync(cancellationToken);
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        var versions = await db.IpDatabaseVersions.AsNoTracking().OrderByDescending(version => version.CreatedAtUtc).Take(500).ToListAsync(cancellationToken);
        var active = await db.IpDatabaseActivations.AsNoTracking().Select(item => item.VersionId).ToListAsync(cancellationToken);
        var tasks = await db.DatabaseUploadTasks.AsNoTracking().OrderByDescending(task => task.StartedAtUtc).Take(100).ToListAsync(cancellationToken);
        var order = registry.Order;
        var providers = order.Providers.Select(provider =>
        {
            var d = provider.Descriptor;
            return new
            {
                d.Id, d.Name, d.Status, d.Enabled, d.LicenseConfirmed, d.Ready, d.SupportsIpv4, d.SupportsIpv6,
                d.Version, d.Message, d.LicenseUrl, d.Attribution, d.LicenseReviewMode, revision = registry.Revision(d.Id),
                versions = versions.Where(version => version.ProviderCode == ProviderCodes.Storage(d.Id)).Select(version => new
                {
                    id = version.Id.ToString(CultureInfo.InvariantCulture), version.Version, version.AddressFamily,
                    format = version.FileFormat, version.SizeBytes, version.Sha256, version.Status,
                    active = active.Contains(version.Id), createdAtUtc = version.CreatedAtUtc,
                    licenseConfirmed = false, licenseReviewMode = "manual"
                }).ToArray(),
                uploads = tasks.Where(task => task.ProviderCode == ProviderCodes.Storage(d.Id)).Select(task => new
                {
                    id = task.Id.ToString(CultureInfo.InvariantCulture), task.State, task.Phase, task.ReceivedBytes,
                    task.ErrorCode, task.StartedAtUtc, task.FinishedAtUtc
                }).ToArray()
            };
        }).ToArray();
        return new { providers, displayOrderRevision = order.Revision, defaultDatabase = ProviderCodes.Public(settings.Current.Query.DefaultDatabase), uploadMaxBytes = uploadOptions.Value.MaxFileBytes };
    }

    public async Task<DatabaseManagementResponse> ReorderAsync(DatabaseOrderRequest request, AdminAccount actor,
        HttpContext context, CancellationToken cancellationToken)
    {
        var knownIds = registry.Providers.Select(provider => provider.Descriptor.Id).ToArray();
        var requestedIds = request.ProviderIds?.Select(id => id is null ? null! : ProviderCodes.Public(id)).ToArray();
        if (!IsCompleteOrder(requestedIds, knownIds) || request.Revision < 0)
            return Fail(400, "INVALID_DATABASE_ORDER", "排序必须包含全部已登记数据库，每项恰好一次，不能重复、缺漏或包含未知项。");
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ConsoleMutation.LockActorAsync(db, actor, cancellationToken, context)) return IdentityChanged();
        var row = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "provider.displayOrder", cancellationToken);
        if ((row?.Revision ?? 0) != request.Revision)
            return Fail(409, "DATABASE_ORDER_CONFLICT", "数据库顺序已被其他操作修改，请刷新后重试。");
        var before = RuntimeProviderRegistry.NormalizeOrder(row is null ? [] : JsonSerializer.Deserialize<string[]>(row.ValueJson), knownIds);
        await SaveSettingAsync(db, "provider.displayOrder", JsonSerializer.Serialize(requestedIds), actor.Id, cancellationToken);
        var nextRevision = checked(request.Revision + 1);
        ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.reorder", "providerOrder", null,
            new { before = new { providerIds = before }, after = new { providerIds = requestedIds }, revision = nextRevision });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        // 提交后直接发布，不让第二次连库失败推迟当前进程中的公开顺序更新。
        registry.PublishOrder(requestedIds!, nextRevision);
        return new(200, ApiResult<object>.Ok(new { displayOrderRevision = nextRevision }, "数据库顺序已保存并生效。"));
    }

    internal static bool IsCompleteOrder(string[]? requested, IReadOnlyCollection<string> known)
    {
        if (requested is null || requested.Length != known.Count || requested.Any(id => id is null)) return false;
        var normalized = requested.Select(ProviderCodes.Public).ToArray();
        var normalizedKnown = known.Select(ProviderCodes.Public).ToHashSet(StringComparer.Ordinal);
        return normalized.Distinct(StringComparer.Ordinal).Count() == normalized.Length && normalized.All(normalizedKnown.Contains);
    }

    public async Task<DatabaseManagementResponse> SetStateAsync(string id, DatabaseStateRequest request, AdminAccount actor,
        HttpContext context, CancellationToken cancellationToken)
    {
        id = ProviderCodes.Public(id);
        var provider = registry.Providers.SingleOrDefault(provider => provider.Descriptor.Id == id);
        if (provider is null) return Fail(404, "UNKNOWN_DATABASE", "指定来源不存在。");
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ConsoleMutation.LockActorAsync(db, actor, cancellationToken, context)) return IdentityChanged();
        var state = await GetStateAsync(db, id, cancellationToken);
        if ((state?.Revision ?? 0) != request.Revision) return Conflict();
        // 共享事务锁内重读配置与来源指针，不能使用另一个进程已经过期的内存快照决策。
        await registry.ReloadAsync(cancellationToken);
        await settings.ReloadAsync(cancellationToken);
        var currentDefault = await ReadDefaultAsync(db, cancellationToken);
        var nextDefault = request.DefaultDatabase is null ? currentDefault : ProviderCodes.Public(request.DefaultDatabase);
        long? publishedRevision = null;
        if (!request.Enabled && nextDefault == id) return Fail(409, "DEFAULT_DATABASE_REQUIRED", "停用默认来源时，请同时选择其他已就绪的默认来源。");
        if (nextDefault != currentDefault)
        {
            var next = registry.Providers.SingleOrDefault(item => item.Descriptor.Id == nextDefault)?.Descriptor;
            using var effective = nextDefault == id && request.Enabled && files.Supports(id) ? await OpenEffectiveAsync(db, id, cancellationToken) : null;
            if (effective is not null) next = effective.Provider.Descriptor;
            if (next is null || !next.Ready || (!next.Enabled && nextDefault != id))
                return Fail(409, "DEFAULT_DATABASE_NOT_READY", "默认来源必须已启用且就绪。");
            await SaveSettingAsync(db, "query.defaultDatabase", JsonSerializer.Serialize(nextDefault), actor.Id, cancellationToken);
            var revision = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == "console.settingsRevision", cancellationToken);
            var nextRevision = (revision?.Revision ?? 1) + 1;
            await SaveSettingAsync(db, "console.settingsRevision", JsonSerializer.Serialize(nextRevision), actor.Id, cancellationToken);
            db.ChangeTracker.Entries<SystemSetting>().Single(entry => entry.Entity.Key == "console.settingsRevision").Entity.Revision = nextRevision;
            publishedRevision = nextRevision;
        }
        await SaveSettingAsync(db, ProviderCodes.EnabledSettingKey(id), JsonSerializer.Serialize(request.Enabled), actor.Id, cancellationToken);
        ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.state", "provider", id,
            new { enabled = request.Enabled, defaultDatabase = nextDefault, revision = request.Revision + 1 });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        registry.PublishState(id, request.Enabled, request.Revision + 1);
        if (publishedRevision.HasValue)
        {
            var snapshot = settings.Current;
            settings.Publish(snapshot with { Revision = publishedRevision.Value, Query = new QueryOptions {
                DefaultDatabase = nextDefault, ProviderTimeoutMs = snapshot.Query.ProviderTimeoutMs,
                TotalTimeoutMs = snapshot.Query.TotalTimeoutMs, MaxConcurrentSources = snapshot.Query.MaxConcurrentSources } });
        }
        return await RefreshCommittedAsync(id, cancellationToken, acceptUnready: true);
    }

    public async Task<DatabaseManagementResponse> ActivateAsync(string id, DatabaseActivationRequest request, AdminAccount actor,
        HttpContext context, CancellationToken cancellationToken)
    {
        id = ProviderCodes.Public(id);
        var storageId = ProviderCodes.Storage(id);
        if (!long.TryParse(request.VersionId, NumberStyles.None, CultureInfo.InvariantCulture, out var versionId) || versionId <= 0)
            return Fail(400, "INVALID_VERSION", "版本标识无效。");
        if (!registry.Providers.Any(provider => provider.Descriptor.Id == id))
            return Fail(404, "UNKNOWN_DATABASE", "指定来源不存在。");
        if (!files.Supports(id)) return Fail(422, "DATABASE_FORMAT_UNSUPPORTED", "此来源的文件读取器尚未实现，暂不支持激活。");
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ConsoleMutation.LockActorAsync(db, actor, cancellationToken, context)) return IdentityChanged();
        var state = await GetStateAsync(db, id, cancellationToken);
        if ((state?.Revision ?? 0) != request.Revision) return Conflict();
        var version = await db.IpDatabaseVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId && item.ProviderCode == storageId, cancellationToken);
        if (version is null) return Fail(404, "VERSION_NOT_FOUND", "此来源的数据版本不存在。");
        if (version.Status != "ready") return Fail(409, "VERSION_NOT_READY", "仅可激活已通过技术校验的版本。");
        ValidateStoragePath(version.StoragePath);
        var digest = await HashFileAsync(version.StoragePath, cancellationToken);
        if (digest != version.Sha256) return Fail(409, "VERSION_HASH_MISMATCH", "版本文件摘要发生变化，已拒绝激活。");
        using (var candidate = new DisposableProvider(files.Validate(id, version.StoragePath, version.AddressFamily)))
        {
            if (!candidate.Provider.Descriptor.Ready) return Fail(409, "VERSION_VALIDATION_FAILED", candidate.Provider.Descriptor.Message);
            await ValidateReadsAsync(candidate.Provider, version.AddressFamily, cancellationToken);
        }
        var families = version.AddressFamily == "dual" ? new[] { "ipv4", "ipv6" } : new[] { version.AddressFamily };
        var currentPaths = await (from active in db.IpDatabaseActivations.AsNoTracking()
            join stored in db.IpDatabaseVersions.AsNoTracking() on active.VersionId equals stored.Id
            where active.ProviderCode == storageId select new { active.AddressFamily, stored.StoragePath }).ToDictionaryAsync(item => item.AddressFamily, item => item.StoragePath, cancellationToken);
        foreach (var family in families) currentPaths[family] = version.StoragePath;
        using (var combined = new DisposableProvider(files.Open(id, currentPaths.GetValueOrDefault("ipv4"), currentPaths.GetValueOrDefault("ipv6"), true)))
        {
            if (!combined.Provider.Descriptor.Ready) return Fail(409, "DATABASE_COMBINATION_NOT_READY", "新版本与保留地址族的组合未通过校验，活动版本未改变。");
        }
        foreach (var family in families)
        {
            var active = await db.IpDatabaseActivations.SingleOrDefaultAsync(item => item.ProviderCode == storageId && item.AddressFamily == family, cancellationToken);
            if (active is null)
            {
                active = new IpDatabaseActivation { ProviderCode = storageId, AddressFamily = family, Revision = 0 };
                db.IpDatabaseActivations.Add(active);
            }
            active.VersionId = version.Id; active.Revision++; active.UpdatedAtUtc = DateTimeOffset.UtcNow; active.UpdatedById = actor.Id;
        }
        var enabled = state is null ? registry.Providers.Single(provider => provider.Descriptor.Id == id).Descriptor.Enabled
            : JsonSerializer.Deserialize<bool>(state.ValueJson);
        await SaveSettingAsync(db, ProviderCodes.EnabledSettingKey(id), JsonSerializer.Serialize(enabled), actor.Id, cancellationToken);
        ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.activate", "databaseVersion", version.Id.ToString(CultureInfo.InvariantCulture),
            new { provider = id, version = version.Version, addressFamily = version.AddressFamily, sha256 = version.Sha256, revision = request.Revision + 1 });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return await RefreshCommittedAsync(id, cancellationToken);
    }

    public async Task<DatabaseManagementResponse> UploadAsync(string id, AdminAccount actor, HttpContext context, CancellationToken cancellationToken)
    {
        id = ProviderCodes.Public(id);
        var storageId = ProviderCodes.Storage(id);
        if (!registry.Providers.Any(provider => provider.Descriptor.Id == id))
            return Fail(404, "UNKNOWN_DATABASE", "指定来源不存在。");
        if (!files.Supports(id)) return Fail(422, "DATABASE_FORMAT_UNSUPPORTED", "此来源的文件读取器尚未实现，暂不支持上传此格式。");
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media) || media.MediaType != "multipart/form-data" ||
            string.IsNullOrWhiteSpace(media.Boundary.Value) || media.Boundary.Value.Length > 128)
            return Fail(400, "INVALID_UPLOAD", "请使用 multipart/form-data 上传一个原始数据库文件。");
        if (!await _uploadGate.WaitAsync(0, cancellationToken)) return Fail(409, "UPLOAD_BUSY", "已有上传任务正在接收或校验，请稍后重试。");
        var taskId = ids.NewId();
        long received = 0;
        string? hash = null;
        var started = false;
        IpLocationDbContext? uploadLock = null;
        var ownsUploadLock = false;
        var stage = "prepareStorage";
        try
        {
            var options = uploadOptions.Value;
            var root = ValidateStorageRoot(options.StorageRoot);
            if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(root);
            // 跨进程只允许一个上传；会话锁随此专用连接关闭而释放，不依赖进程内 Semaphore。
            stage = "acquireUploadLock";
            uploadLock = await Factory.CreateDbContextAsync(cancellationToken);
            await uploadLock.Database.OpenConnectionAsync(cancellationToken);
            if (!await uploadLock.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(4933277946142573) AS \"Value\"").SingleAsync(cancellationToken))
                return Fail(409, "UPLOAD_BUSY", "已有上传任务正在接收或校验，请稍后重试。");
            ownsUploadLock = true;
            var used = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Sum(path => new FileInfo(path).Length);
            if (used >= options.MaxTotalBytes) return Fail(409, "UPLOAD_QUOTA_EXCEEDED", "数据库存储限额已用尽，文件不会自动删除。");
            stage = "createTask";
            await using (var db = await Factory.CreateDbContextAsync(cancellationToken))
            {
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                if (!await ConsoleMutation.LockActorAsync(db, actor, cancellationToken, context)) return IdentityChanged();
                db.DatabaseUploadTasks.Add(new DatabaseUploadTask { Id = taskId, ProviderCode = storageId, OperatorId = actor.Id, StartedAtUtc = DateTimeOffset.UtcNow });
                ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.upload.start", "databaseUpload", taskId.ToString(CultureInfo.InvariantCulture), new { provider = id });
                await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); started = true;
            }
            var extension = UploadExtension(id);
            var path = Path.Combine(root, taskId.ToString(CultureInfo.InvariantCulture) + extension);
            stage = "receiveMultipart";
            var reception = await ReceiveMultipartAsync(context.Request.Body, HeaderUtilities.RemoveQuotes(media.Boundary).Value!,
                extension, options.MaxFileBytes, options.MaxTotalBytes - used, () => CreateUploadFile(path),
                (phase, bytes) => { stage = phase; received = bytes; }, cancellationToken);
            hash = reception.Sha256;
            var fields = reception.Fields;
            stage = "validateMetadata";
            var family = fields.GetValueOrDefault("addressFamily", id is "ip2location" or ProviderCodes.Cz88 ? "ipv4" : "dual");
            if (family is not ("ipv4" or "ipv6" or "dual") || id == "ip2region" && family == "dual" || id is "ip2location" or ProviderCodes.Cz88 && family != "ipv4")
                throw new UploadRejectedException("INVALID_ADDRESS_FAMILY");
            using var opened = new DisposableProvider(files.Validate(id, path, family));
            var descriptor = opened.Provider.Descriptor;
            if (!descriptor.Ready || descriptor.Version is null || family is "ipv6" or "dual" && !descriptor.SupportsIpv6)
                throw new UploadRejectedException("DATABASE_VALIDATION_FAILED");
            if (id != "ip2region" && family != (descriptor.SupportsIpv6 ? "dual" : "ipv4"))
                throw new UploadRejectedException("ADDRESS_FAMILY_MISMATCH");
            stage = "validateSamples";
            await ValidateReadsAsync(opened.Provider, family, cancellationToken);
            var versionId = ids.NewId();
            stage = "commitVersion";
            await using (var db = await Factory.CreateDbContextAsync(cancellationToken))
            {
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                if (!await ConsoleMutation.LockActorAsync(db, actor, cancellationToken, context)) throw new UploadRejectedException("CONSOLE_IDENTITY_CHANGED");
                var task = await db.DatabaseUploadTasks.SingleAsync(task => task.Id == taskId, cancellationToken);
                db.IpDatabaseVersions.Add(new IpDatabaseVersion { Id = versionId, ProviderCode = storageId, Version = descriptor.Version,
                    FileFormat = extension[1..], AddressFamily = family, Sha256 = hash, SizeBytes = received, StoragePath = path,
                    LicenseConfirmed = false, LicenseEvidenceJson = JsonSerializer.Serialize(new { reviewMode = "manual", systemVerified = false }),
                    Status = "ready", CreatedById = actor.Id, CreatedAtUtc = DateTimeOffset.UtcNow, ValidatedAtUtc = DateTimeOffset.UtcNow });
                task.ReceivedBytes = received; task.Sha256 = hash; task.VersionId = versionId; task.State = "ready"; task.Phase = "validated"; task.FinishedAtUtc = DateTimeOffset.UtcNow;
                ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.upload.ready", "databaseVersion", versionId.ToString(CultureInfo.InvariantCulture),
                    new { provider = id, version = descriptor.Version, addressFamily = family, sha256 = hash, sizeBytes = received });
                await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
            }
            return new(200, ApiResult<object>.Ok(new { versionId = versionId.ToString(CultureInfo.InvariantCulture) }, "文件已校验，尚未激活。"));
        }
        catch (Exception exception) when (exception is UploadRejectedException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            var code = exception is UploadRejectedException rejected ? rejected.Code : exception is OperationCanceledException ? "UPLOAD_INTERRUPTED" : "UPLOAD_FAILED";
            logger.LogWarning("数据库上传失败 Stage={Stage} ExceptionType={ExceptionType} HResult={HResult} TraceId={TraceId} TaskId={TaskId}",
                stage, exception.GetType().Name, exception.HResult, context.TraceIdentifier, taskId);
            if (started) await MarkFailedAsync(taskId, received, hash, code, stage, context);
            return Fail(400, code, "上传未完成或文件未通过校验；现有活动版本未改变，已接收文件保留并计入占用。");
        }
        finally
        {
            try
            {
                if (uploadLock is not null)
                {
                    try
                    {
                        if (ownsUploadLock) await uploadLock.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(4933277946142573)", CancellationToken.None);
                    }
                    finally { await uploadLock.DisposeAsync(); }
                }
            }
            finally { _uploadGate.Release(); }
        }
    }

    public async Task RecoverInterruptedUploadsAsync(CancellationToken cancellationToken)
    {
        var factory = services.GetService<IDbContextFactory<IpLocationDbContext>>();
        if (factory is null) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var ownsLock = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(4933277946142573) AS \"Value\"").SingleAsync(cancellationToken);
        if (!ownsLock) return;
        try
        {
            var interrupted = await db.DatabaseUploadTasks.Where(task => task.State == "receiving" || task.State == "validating").ToListAsync(cancellationToken);
            foreach (var task in interrupted)
            {
                var partial = Path.Combine(ValidateStorageRoot(uploadOptions.Value.StorageRoot), task.Id.ToString(CultureInfo.InvariantCulture) + UploadExtension(task.ProviderCode));
                if (File.Exists(partial) && new FileInfo(partial).LinkTarget is null) task.ReceivedBytes = new FileInfo(partial).Length;
                task.State = "failed"; task.Phase = "interrupted"; task.ErrorCode = "UPLOAD_INTERRUPTED"; task.FinishedAtUtc = DateTimeOffset.UtcNow;
                db.AdminAuditEvents.Add(new AdminAuditEvent { Id = ids.NewId(), ActorId = task.OperatorId,
                    Action = "database.upload.interrupted", TargetType = "databaseUpload", TargetId = task.Id.ToString(CultureInfo.InvariantCulture),
                    DifferenceJson = "{\"errorCode\":\"UPLOAD_INTERRUPTED\"}", Result = "failed", TraceId = "upload-recovery", OccurredAtUtc = DateTimeOffset.UtcNow });
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(4933277946142573)", CancellationToken.None); }
    }

    public async Task RecordDeniedAsync(string id, string reasonCode, HttpContext context, CancellationToken cancellationToken, string targetType = "provider")
    {
        if (targetType == "provider") id = ProviderCodes.Public(id);
        // 业务变更已回滚后独立可靠记录拒绝原因；只保存固定机器码，不保存请求正文、私有路径或异常原文。
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.operation.denied", targetType, id,
            new { reasonCode }, "denied");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(long taskId, long received, string? hash, string code, string stage, HttpContext context)
    {
        // 客户端中断后仍尝试可靠记录结果；不能沿用已经取消的请求令牌。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db = await Factory.CreateDbContextAsync(deadline.Token);
        var task = await db.DatabaseUploadTasks.SingleAsync(task => task.Id == taskId, deadline.Token);
        task.ReceivedBytes = received; task.Sha256 = hash; task.State = "failed"; task.Phase = stage; task.ErrorCode = code; task.FinishedAtUtc = DateTimeOffset.UtcNow;
        ConsoleMutation.AddAudit(db, ids, clientIp, context, "database.upload.failed", "databaseUpload", taskId.ToString(CultureInfo.InvariantCulture),
            new { errorCode = code, sizeBytes = received }, "failed");
        await db.SaveChangesAsync(deadline.Token);
    }

    private async Task<DatabaseManagementResponse> RefreshCommittedAsync(string id, CancellationToken cancellationToken, bool acceptUnready = false)
    {
        try
        {
            await settings.ReloadAsync(cancellationToken); await registry.ReloadAsync(cancellationToken);
            var provider = registry.Providers.Single(provider => provider.Descriptor.Id == id).Descriptor;
            if (provider.Enabled && !provider.Ready && !acceptUnready)
                return Fail(503, "DATABASE_CHANGE_COMMITTED_REFRESH_PENDING", "活动指针已保存，但读取器尚未就绪；请查看来源状态，后台将自动重试。");
            return new(200, ApiResult<object>.Ok(new { provider.Enabled, provider.Ready, provider.Status },
                provider.Enabled && !provider.Ready ? "启用状态已保存；数据文件或读取器尚未就绪。" : "已生效。"));
        }
        catch (Exception) { return Fail(503, "DATABASE_CHANGE_COMMITTED_REFRESH_PENDING", "变更和审计已提交，但运行状态刷新尚未完成；请刷新状态后核对，后台将自动重试。"); }
    }

    private async Task<string> ReadDefaultAsync(IpLocationDbContext db, CancellationToken cancellationToken)
    {
        var setting = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(item => item.Key == "query.defaultDatabase", cancellationToken);
        return ProviderCodes.Public(setting is null ? settings.Current.Query.DefaultDatabase : JsonSerializer.Deserialize<string>(setting.ValueJson)!);
    }
    private static Task<SystemSetting?> GetStateAsync(IpLocationDbContext db, string id, CancellationToken cancellationToken)
    {
        var key = ProviderCodes.EnabledSettingKey(id);
        return db.SystemSettings.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
    }
    private static async Task SaveSettingAsync(IpLocationDbContext db, string key, string value, long actorId, CancellationToken cancellationToken)
    {
        var item = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (item is null) { item = new SystemSetting { Key = key, Revision = 0 }; db.SystemSettings.Add(item); }
        item.ValueJson = value; item.Revision++; item.UpdatedById = actorId; item.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
    private async Task<DisposableProvider> OpenEffectiveAsync(IpLocationDbContext db, string id, CancellationToken cancellationToken)
    {
        var storageId = ProviderCodes.Storage(id);
        var pointers = await (from active in db.IpDatabaseActivations.AsNoTracking() join version in db.IpDatabaseVersions.AsNoTracking()
            on active.VersionId equals version.Id where active.ProviderCode == storageId select new { active.AddressFamily, version.StoragePath }).ToListAsync(cancellationToken);
        return new(files.Open(id, pointers.SingleOrDefault(item => item.AddressFamily == "ipv4")?.StoragePath,
            pointers.SingleOrDefault(item => item.AddressFamily == "ipv6")?.StoragePath, pointers.Count > 0));
    }
    private string ValidateStorageRoot(string path)
    {
        path = Path.GetFullPath(path);
        var current = new DirectoryInfo(environment.ContentRootPath);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(current.FullName, "frontend")) && IsWithin(path, current.FullName))
                throw new InvalidDataException("Storage must be outside repository.");
            current = current.Parent;
        }
        if (!string.IsNullOrWhiteSpace(environment.WebRootPath) && IsWithin(path, environment.WebRootPath)) throw new InvalidDataException("Storage must be private.");
        current = new DirectoryInfo(path);
        while (current is not null) { if (current.Exists && current.LinkTarget is not null) throw new InvalidDataException("Symbolic links are not accepted."); current = current.Parent; }
        return path;
    }
    private void ValidateStoragePath(string path)
    {
        var root = ValidateStorageRoot(uploadOptions.Value.StorageRoot);
        if (!IsWithin(path, root) || new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Invalid version storage path.");
    }
    private static bool IsWithin(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.GetFullPath(path) == Path.GetFullPath(root);
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    internal static FileStream CreateUploadFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, BufferSize = 65536, Options = FileOptions.Asynchronous };
        // 在创建时设置权限，避免排他打开后再次按路径打开文件设置权限。
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    internal static async Task<UploadReception> ReceiveMultipartAsync(Stream body, string boundary, string extension,
        long maxFileBytes, long remainingBytes, Func<Stream> createFile, Action<string, long> progress, CancellationToken cancellationToken)
    {
        var reader = new MultipartReader(boundary, body)
        { HeadersCountLimit = 16, HeadersLengthLimit = 8192, BodyLengthLimit = maxFileBytes + 16384 };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileSeen = false; var sectionCount = 0; long received = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
        {
            if (++sectionCount > 2) throw new UploadRejectedException("INVALID_UPLOAD_FIELDS");
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || disposition.DispositionType != "form-data")
                throw new UploadRejectedException("INVALID_UPLOAD");
            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
            {
                var filename = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "";
                if (fileSeen || name != "file" || !filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    throw new UploadRejectedException("INVALID_UPLOAD_FORMAT");
                fileSeen = true;
                progress("createFile", received);
                await using var output = createFile();
                progress("receiveFile", received);
                var buffer = new byte[65536]; int count;
                while ((count = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (received + count > maxFileBytes || received + count > remainingBytes)
                        throw new UploadRejectedException("UPLOAD_QUOTA_EXCEEDED");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    sha.AppendData(buffer, 0, count); received += count; progress("receiveFile", received);
                }
                await output.FlushAsync(cancellationToken);
            }
            else
            {
                progress("readFields", received);
                if (name != "addressFamily" || fields.ContainsKey(name))
                    throw new UploadRejectedException("INVALID_UPLOAD_FIELDS");
                using var text = new MemoryStream(); var buffer = new byte[1024]; int count;
                while ((count = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                { if (text.Length + count > 8192) throw new UploadRejectedException("INVALID_UPLOAD_FIELDS"); text.Write(buffer, 0, count); }
                fields[name] = Encoding.UTF8.GetString(text.ToArray());
            }
            progress("receiveMultipart", received);
        }
        if (!fileSeen || received == 0) throw new UploadRejectedException("UPLOAD_FILE_REQUIRED");
        return new(received, Convert.ToHexStringLower(sha.GetHashAndReset()), fields);
    }
    internal sealed record UploadReception(long ReceivedBytes, string Sha256, Dictionary<string, string> Fields);
    private static string UploadExtension(string id) => ProviderCodes.Public(id) switch { "ip2region" => ".xdb", ProviderCodes.Cz88 => ".dat", _ => ".mmdb" };
    private static async Task ValidateReadsAsync(IIpDataProvider provider, string family, CancellationToken cancellationToken)
    {
        // 文件头与来源类型之外，再验证地址族的受控样本可解析；不把抽样宣称为全库内容正确性证明。
        var samples = family == "ipv4" ? new[] { "8.8.8.8", "1.1.1.1" }
            : family == "ipv6" ? new[] { "2001:4860:4860::8888" } : new[] { "8.8.8.8", "2001:4860:4860::8888" };
        foreach (var ip in samples)
        {
            var result = await provider.QueryAsync(IPAddress.Parse(ip), cancellationToken);
            if (result.Status is not ("success" or "notFound")) throw new UploadRejectedException("DATABASE_VALIDATION_FAILED");
        }
    }
    private static DatabaseManagementResponse Fail(int status, string code, string message) => new(status, ApiResult<object>.Fail(code, message));
    private static DatabaseManagementResponse Conflict() => Fail(409, "DATABASE_REVISION_CONFLICT", "来源状态已被其他操作修改，请刷新后重试。");
    private static DatabaseManagementResponse IdentityChanged() => Fail(401, "CONSOLE_IDENTITY_CHANGED", "管理员身份已变更，请重新登录。");
    internal sealed class UploadRejectedException(string code) : Exception(code) { public string Code { get; } = code; }
    private sealed class DisposableProvider(IIpDataProvider provider) : IDisposable
    { public IIpDataProvider Provider { get; } = provider; public void Dispose() => (Provider as IDisposable)?.Dispose(); }
}
