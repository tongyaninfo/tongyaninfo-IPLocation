using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// 仅在启动参数明确指定时加载运行配置；不隐式读取个人路径或未显式指定的文件。
var settingsFile = builder.Configuration["Runtime:SettingsFile"];
if (!string.IsNullOrWhiteSpace(settingsFile))
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(settingsFile), optional: false, reloadOnChange: false);
    // 明确命令行参数优先于运行配置，便于使用不同本机监听端口而不改文件。
    builder.Configuration.AddCommandLine(args);
}

if (builder.Configuration.GetValue<bool>("Admin:Initialize") || builder.Configuration.GetValue<bool>("Admin:Reset"))
{
    Environment.ExitCode = await AdminInitializationCommand.RunAsync(builder.Configuration);
    return;
}

// 禁用宿主由环境开关隐式加入的转发处理中间件，只使用下方明确的信任名单。
builder.WebHost.UseSetting("forwardedHeaders_enabled", "false");
builder.Configuration["ForwardedHeaders_Enabled"] = "false";
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Encoder = ApiJsonEncoding.Encoder)
    .ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = _ => new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(
        ApiResult<object>.Fail("INVALID_REQUEST", "请求内容无效。")));
// Minimal API 与 WriteAsJsonAsync 使用独立选项，需与 MVC、查询格式器保持一致。
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Encoder = ApiJsonEncoding.Encoder);
builder.Services.AddOptions<QueryOptions>().Bind(builder.Configuration.GetSection("Query"))
    .Validate(options => options.ProviderTimeoutMs is > 0 and <= 60_000 && options.TotalTimeoutMs is > 0 and <= 120_000 &&
                         options.MaxConcurrentSources is > 0 and <= 6, "查询截止时间或并发配置超出允许范围。")
    .ValidateOnStart();
builder.Services.AddConsoleAccess(builder.Configuration);
builder.Services.AddSingleton<IClientIpService, ClientIpService>();
builder.Services.AddSingleton<IIpQueryService, IpQueryService>();
builder.Services.AddSingleton<FrontendHost>();
builder.Services.AddIpDataProviders();
builder.Services.AddAccessLogging(builder.Configuration);
builder.Services.AddPostgreSqlPersistence(builder.Configuration);
builder.Services.AddConsoleAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IConsoleRuntimeSettings, ConsoleRuntimeSettings>();
// 公共查询站按服务端安装实例计数，浏览器访客不会各自生成安装身份。
builder.Services.AddSingleton(new Tongyan.Support.Client.Models.SupportOptions("ip",
    // 尚无统一后端版本源，不能把 SDK 默认程序集版本或前端包版本冒充产品发布版本。
    builder.Configuration["Support:ProductVersion"] ?? "unknown",
    builder.Configuration["Support:StateDirectory"] ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tongyan", "IpLocation", "support"),
    Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "docker" : "server",
    new Uri("https://api.tongyaninfo.com"), builder.Configuration.GetValue("Support:TelemetryEnabled", true)));
builder.Services.AddSingleton(services => new Tongyan.Support.Client.Service.ProductSupportClient(
    services.GetRequiredService<Tongyan.Support.Client.Models.SupportOptions>(), services.GetRequiredService<IHttpClientFactory>().CreateClient("TongyanSupport")));
builder.Services.AddHttpClient("TongyanSupport", client => client.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddHostedService<IpLocation.Api.Workers.SupportHeartbeatWorker>();
builder.Services.AddScoped<IConsoleSettingsService, ConsoleSettingsService>();
builder.Services.AddOptions<TrustedProxyOptions>().Bind(builder.Configuration.GetSection("TrustedProxies"));
builder.Services.AddScoped<IConsoleReadService, ConsoleReadService>();
builder.Services.AddPublicMcp();

var app = builder.Build();
try { await app.Services.GetRequiredService<IConsoleRuntimeSettings>().ReloadAsync(); }
catch (Exception exception)
{
    app.Logger.LogWarning("后台运行设置加载失败，后台入口保持关闭，类型：{Type}", exception.GetType().Name);
}
// 预装只读来源，避免第一位访客承担读取器初始化；此处不访问后台数据库。
_ = app.Services.GetRequiredService<IIpQueryService>();
app.UseMiddleware<ApiExceptionMiddleware>();

var trusted = builder.Configuration.GetSection("TrustedProxies").Get<TrustedProxyOptions>() ?? new();
if (trusted.ForwardLimit is < 1 or > 10) throw new InvalidOperationException("可信代理转发层数必须在 1 到 10 之间。");
if (trusted.KnownProxies.Length > 0 || trusted.KnownIPNetworks.Length > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = trusted.ForwardLimit
    };
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var entry in trusted.KnownProxies)
    {
        if (!IpAddressParser.TryParse(entry, out var proxy)) throw new InvalidOperationException("可信代理地址配置无效。");
        forwarded.KnownProxies.Add(proxy);
    }
    foreach (var entry in trusted.KnownIPNetworks)
    {
        if (!System.Net.IPNetwork.TryParse(entry, out var network)) throw new InvalidOperationException("可信代理网段配置无效。");
        forwarded.KnownIPNetworks.Add(network);
    }
    app.UseForwardedHeaders(forwarded);
}

// 日志只做有界非阻塞入队；白名单先于静态文件和全部后台入口执行。
app.UseMiddleware<AccessLogMiddleware>();
app.UseMiddleware<ConsoleAccessMiddleware>();
app.UseRouting();
app.UseCors();
app.UseMiddleware<McpRequestMetadataMiddleware>();
// 公共纯 IP 路径不读取管理员 Cookie 或等待身份数据库。
// 分支的中间件标记不会写回根管线，必须阻止 WebApplication 再自动插入全局认证。
((IApplicationBuilder)app).Properties["__AuthenticationMiddlewareSet"] = true;
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/console", StringComparison.OrdinalIgnoreCase),
    // HTTP 与 HTTPS 共用现有白名单、身份与防伪校验，不再按传输协议拦截后台。
    branch => branch.UseAuthentication());
app.UseAuthorization();
app.UseRateLimiter();
var frontend = app.Services.GetRequiredService<FrontendHost>();
if (Directory.Exists(frontend.DistPath))
{
    app.UseFrontendStaticAssets(frontend.DistPath);
}

app.MapControllers();
app.MapMcp("/mcp").AllowAnonymous().RequireCors(PublicMcpExtensions.CorsPolicy);
app.MapGet("/console/{**path}", async (HttpContext context) => await frontend.WriteIndexAsync(context));
app.MapMethods("/api/console/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"],
    async (HttpContext context, IConsoleIdentityService identities) =>
    {
        var access = await identities.GetAccessAsync(context);
        if (access.StatusCode != 200) return Results.Json(access.Result, statusCode: access.StatusCode);
        return Results.Json(ApiResult<object>.Fail("CONSOLE_NOT_IMPLEMENTED", "此管理功能尚未实现。"), statusCode: 501);
    });
app.MapGet("/health", () => Results.Ok(ApiResult<object>.Ok(new
{
    status = "alive",
    databaseConfigured = builder.Configuration.GetValue<bool>("Database:Enabled"),
    databaseReadiness = builder.Configuration.GetValue<bool>("Database:Enabled") ? "notChecked" : "disabled"
}, "进程可响应；此端点不连接数据库，不证明来源或后台已就绪。")));
app.Run();

/// <summary>提供真实 HTTP 宿主供 WebApplicationFactory 集成测试。</summary>
public partial class Program;
