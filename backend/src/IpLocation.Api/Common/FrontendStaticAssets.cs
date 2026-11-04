using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace IpLocation.Api.Common;

/// <summary>静态文本使用构建时压缩副本；条件请求和范围响应继续交给框架处理。</summary>
public static partial class FrontendStaticAssets
{
    private static readonly object RepresentationKey = new();

    public static void UseFrontendStaticAssets(this WebApplication app, string distPath)
    {
        var files = new PhysicalFileProvider(distPath);
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        var types = new EncodedContentTypes();

        // 此管线必须位于后台白名单之后。只改静态资源路径，不压缩 API、纯 IP 或身份响应。
        app.Use(async (context, next) =>
        {
            var originalPath = context.Request.Path;
            var path = originalPath.Value ?? "";
            var isSidecar = path.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            var resourcePath = isSidecar ? path[..path.LastIndexOf('.')] : path;
            if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) || !IsStaticPath(resourcePath))
            {
                await next(context);
                return;
            }

            // 压缩副本只供内容协商使用，不能以普通下载绕过原始路径的访问规则。
            if (isSidecar)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }

            if (!files.GetFileInfo(path).Exists || !IsTextAsset(path))
            {
                await next(context);
                return;
            }

            var encoding = SelectEncoding(context, files, path);
            if (encoding is null)
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                context.Response.Headers.CacheControl = "no-store";
                AddEncodingVary(context.Response);
                return;
            }

            context.Items[RepresentationKey] = new Representation(path, encoding);
            if (encoding != "identity") context.Request.Path = path + (encoding == "br" ? ".br" : ".gz");
            try { await next(context); }
            finally
            {
                context.Request.Path = originalPath;
                context.Items.Remove(RepresentationKey);
            }
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = prepared =>
            {
                var context = prepared.Context;
                var representation = context.Items[RepresentationKey] as Representation;
                var path = representation?.Path ?? context.Request.Path.Value ?? "";
                context.Response.Headers.CacheControl = CachePolicy(path);
                if (IsTextAsset(path)) AddEncodingVary(context.Response);
                if (representation is { Encoding: not "identity" })
                    context.Response.Headers.ContentEncoding = representation.Encoding;
            }
        });
    }

    private static string? SelectEncoding(HttpContext context, IFileProvider files, string path)
    {
        if (!StringWithQualityHeaderValue.TryParseList([context.Request.Headers.AcceptEncoding.ToString()], out var values))
            return "identity";

        double? ExplicitQuality(string name) => values
            .Where(value => value.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(value => (double?)(value.Quality ?? 1)).DefaultIfEmpty(null).Max();

        var wildcard = ExplicitQuality("*");
        var identity = ExplicitQuality("identity");
        var identityAllowed = (identity ?? (wildcard == 0 ? 0 : 1)) > 0;
        // 不在压缩表示上计算字节范围；拒绝 identity 的客户端需重新请求完整压缩表示。
        if (context.Request.Headers.ContainsKey(HeaderNames.Range)) return identityAllowed ? "identity" : null;

        var choices = new List<(string Encoding, double Quality, int Priority)>();
        foreach (var (encoding, suffix, priority) in new[] { ("br", ".br", 2), ("gzip", ".gz", 1) })
        {
            var quality = ExplicitQuality(encoding) ?? wildcard ?? 0;
            if (quality > 0 && files.GetFileInfo(path + suffix).Exists) choices.Add((encoding, quality, priority));
        }
        // 未列出的 identity 是兼容回退；显式 identity 则参与客户端偏好比较。
        if (identityAllowed) choices.Add(("identity", identity ?? -1, 0));
        return choices.OrderByDescending(choice => choice.Quality).ThenByDescending(choice => choice.Priority)
            .Select(choice => choice.Encoding).FirstOrDefault();
    }

    private static bool IsStaticPath(string path) => path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/brand/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/logos/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/console-assets/", StringComparison.OrdinalIgnoreCase) || path.Equals("/theme-init.js", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextAsset(string path) => Path.GetExtension(path).ToLowerInvariant() is ".js" or ".css" or ".svg" or ".html";

    private static string CachePolicy(string path) => path.StartsWith("/assets/console-", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/console-assets/", StringComparison.OrdinalIgnoreCase) || !FingerprintedAsset().IsMatch(path)
        ? "no-store" : "public,max-age=31536000,immutable";

    private static void AddEncodingVary(HttpResponse response)
    {
        if (!response.Headers.Vary.ToString().Split(',').Any(value => value.Trim().Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
            response.Headers.Append(HeaderNames.Vary, "Accept-Encoding");
    }

    [GeneratedRegex(@"^/assets/[^/]+[-.][A-Za-z0-9_-]{8,}\.(?:js|css|svg|webp|png|avif|ico|woff2?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintedAsset();

    private sealed record Representation(string Path, string Encoding);

    private sealed class EncodedContentTypes : IContentTypeProvider
    {
        private readonly FileExtensionContentTypeProvider _original = new();

        public bool TryGetContentType(string subpath, out string contentType)
        {
            if (subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                subpath = subpath[..subpath.LastIndexOf('.')];
            return _original.TryGetContentType(subpath, out contentType!);
        }
    }
}
