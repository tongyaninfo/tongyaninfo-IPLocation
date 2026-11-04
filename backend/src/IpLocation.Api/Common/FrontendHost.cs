using Microsoft.Net.Http.Headers;

namespace IpLocation.Api.Common;

/// <summary>生产由 API 同源托管前端；未构建时返回真实未就绪提示。</summary>
public sealed class FrontendHost(IWebHostEnvironment environment, IConfiguration configuration)
{
    public string DistPath { get; } = ResolveDist(environment.ContentRootPath, configuration["Frontend:DistPath"]);

    public async Task WriteIndexAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var index = Path.Combine(DistPath, "index.html");
        if (!File.Exists(index))
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>站点尚未就绪</title><body><h1>前端尚未构建</h1><p>请先构建前端，再通过同源服务访问。</p></body></html>");
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index, context.RequestAborted);
    }

    public static bool PrefersHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.Where(value => value is not null).Select(value => value!).ToArray();
        if (!MediaTypeHeaderValue.TryParseList(accept, out var values)) return false;
        var ranges = values.Select((value, index) =>
            (Media: value.MediaType.Value ?? "", Quality: value.Quality ?? 1, Index: index)).ToArray();
        var html = ranges.Where(item => item.Media.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                                       item.Media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Quality).ThenBy(item => item.Index).FirstOrDefault();
        // 单独通配符保持纯 IP 协议；只有明确接受 HTML 才可能返回页面。
        if (html.Media is null || html.Quality <= 0) return false;

        // 纯文本的质量先按最具体媒体范围确定，不能用 */* 推翻 text/plain;q=0。
        var plain = ranges.Select(item => (item.Quality, item.Index, Specificity: item.Media.ToLowerInvariant() switch
            {
                "text/plain" => 2,
                "text/*" => 1,
                "*/*" => 0,
                _ => -1
            })).Where(item => item.Specificity >= 0)
            .OrderByDescending(item => item.Specificity).ThenByDescending(item => item.Quality)
            .ThenBy(item => item.Index).FirstOrDefault((Quality: -1d, Index: int.MaxValue, Specificity: -1));
        // XML 等未提供的格式不参与两种实际表示之间的比较。
        return html.Quality > plain.Quality ||
               (html.Quality == plain.Quality && (plain.Specificity < 2 || html.Index < plain.Index));
    }

    private static string ResolveDist(string contentRoot, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);

        var current = new DirectoryInfo(contentRoot);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "frontend")) && File.Exists(Path.Combine(current.FullName, "AGENTS.md")))
                return Path.GetFullPath(Path.Combine(current.FullName, configuredPath ?? "frontend/dist"));
            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(contentRoot, configuredPath ?? "frontend/dist"));
    }
}
