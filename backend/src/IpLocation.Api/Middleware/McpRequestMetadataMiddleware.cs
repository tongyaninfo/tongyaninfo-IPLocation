using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Middleware;

/// <summary>公开 MCP 请求体有界读取；只提取固定上下文字段，不记录原始请求、任意元数据或聊天内容。</summary>
public sealed class McpRequestMetadataMiddleware(RequestDelegate next, ILogger<McpRequestMetadataMiddleware> logger)
{
    public const int MaxRequestBytes = 16 * 1024;

    public async Task InvokeAsync(HttpContext http)
    {
        if (!PublicMcpExtensions.IsMcpPath(http.Request.Path)) { await next(http); return; }
        http.Response.Headers.CacheControl = "no-store";
        PublicMcpExtensions.WriteContext(http, new(ProtocolVersion: http.Request.Headers["MCP-Protocol-Version"].ToString()));
        var original = http.Request.Body;
        var responseBody = http.Response.Body;
        using var errorCapture = new McpResponseCaptureStream(responseBody,
            () => PublicMcpExtensions.ShouldCapture(http) && http.Response.StatusCode >= 400 &&
                  PublicMcpExtensions.ReadContext(http).Output is null, McpPayloads.MaxOutputBytes);
        http.Response.Body = errorCapture;
        using var buffer = new MemoryStream();
        try
        {
            if (HttpMethods.IsPost(http.Request.Method))
            {
                var bytes = new byte[MaxRequestBytes + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var read = await original.ReadAsync(bytes.AsMemory(length), http.RequestAborted);
                    if (read == 0) break;
                    length += read;
                }
                if (length > MaxRequestBytes)
                {
                    PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { ResultCode = "MCP_REQUEST_TOO_LARGE", ResultStatus = "protocolError", IsError = true, Truncated = true });
                    await WriteErrorAsync(http, 413, -32600, "MCP 请求不得超过 16 KiB。");
                    return;
                }
                buffer.Write(bytes, 0, length);
                buffer.Position = 0;
                http.Request.Body = buffer;
                try
                {
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 32 });
                    if (HasDuplicateProperties(document.RootElement))
                    {
                        PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { ResultCode = "RPC_-32600", ResultStatus = "protocolError", IsError = true });
                        await WriteErrorAsync(http, 400, -32600, "MCP JSON 属性不得重复。");
                        return;
                    }
                    Capture(http, document.RootElement);
                }
                catch (JsonException)
                {
                    PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { ResultCode = "RPC_-32700", ResultStatus = "protocolError", IsError = true });
                    await WriteErrorAsync(http, 400, -32700, "MCP JSON 请求无效。");
                    return;
                }
            }
            await next(http);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { ResultCode = "CANCELLED", ResultStatus = "cancelled", IsError = true });
            throw;
        }
        catch (Exception exception) when (!http.Response.HasStarted)
        {
            logger.LogWarning("公开 MCP 请求处理失败，异常类型：{ExceptionType}", exception.GetType().Name);
            PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { ResultCode = "INTERNAL_ERROR", ResultStatus = "error", IsError = true });
            await WriteErrorAsync(http, 500, -32603, "服务暂时无法完成请求。");
        }
        finally
        {
            http.Request.Body = original;
            http.Response.Body = responseBody;
            CaptureTransportError(http, errorCapture);
            var snapshot = PublicMcpExtensions.ReadContext(http);
            if (http.RequestAborted.IsCancellationRequested)
                PublicMcpExtensions.WriteContext(http, snapshot with { ResultCode = "CANCELLED", ResultStatus = "cancelled", IsError = true });
            else if (snapshot.ResultCode is null)
                PublicMcpExtensions.WriteContext(http, snapshot with { ResultCode = http.Response.StatusCode >= 400 ? "HTTP_" + http.Response.StatusCode : "OK",
                    ResultStatus = http.Response.StatusCode >= 400 ? "protocolError" : "success", IsError = http.Response.StatusCode >= 400 });
        }
    }

    private static void Capture(HttpContext http, JsonElement root)
    {
        if (!PublicMcpExtensions.ShouldCapture(http) || root.ValueKind != JsonValueKind.Object) return;
        var parameters = Object(root, "params");
        var metadata = Object(parameters, "_meta");
        var client = Object(metadata, "io.modelcontextprotocol/clientInfo");
        if (client.ValueKind != JsonValueKind.Object) client = Object(parameters, "clientInfo");
        var arguments = Object(parameters, "arguments");
        var version = Text(metadata, "io.modelcontextprotocol/protocolVersion") ?? Text(parameters, "protocolVersion") ?? TextHeader(http);
        string? requestId = null;
        if (root.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            requestId = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText();
        var rawIp = Text(arguments, "ip");
        var method = Text(root, "method");
        // 工具只记录arguments；握手等方法记录params，由统一快照清洗器移除_meta和凭据。
        JsonElement input;
        if (method == "tools/call" && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var supplied)) input = supplied.Clone();
        else if (method != "tools/call" && parameters.ValueKind == JsonValueKind.Object) input = parameters.Clone();
        else input = JsonSerializer.SerializeToElement(new { });
        PublicMcpExtensions.WriteContext(http, new(ProtocolVersion: version, Method: method, RequestId: requestId,
            ToolName: Text(parameters, "name"), ClientName: Text(client, "name"), ClientVersion: Text(client, "version"),
            Ip: IpAddressParser.TryParse(rawIp, out var address) ? address.ToString() : null, Database: Text(arguments, "db"), Input: input));
    }

    private static string? TextHeader(HttpContext http) => http.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
    private static void CaptureTransportError(HttpContext http, McpResponseCaptureStream capture)
    {
        var snapshot = PublicMcpExtensions.ReadContext(http);
        if (snapshot.Output is not null || capture.Captured.IsEmpty) return;
        if (!capture.Truncated)
        {
            try
            {
                using var document = JsonDocument.Parse(capture.Captured, new JsonDocumentOptions { MaxDepth = 32 });
                var value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out var error)) value = error;
                string? code = null;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("code", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var rpcCode))
                    code = "RPC_" + rpcCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                PublicMcpExtensions.WriteContext(http, snapshot with { Output = value.Clone(), ResultCode = code ?? snapshot.ResultCode,
                    ResultStatus = "protocolError", IsError = true });
                return;
            }
            catch (JsonException) { }
        }
        // 非JSON或超限传输错误不保存原始HTML/碎片，也不伪造一个完整JSON返回值。
        PublicMcpExtensions.WriteContext(http, snapshot with { OutputCaptured = true, OutputTruncated = true });
    }
    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) if (!names.Add(item.Name) || HasDuplicateProperties(item.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
    private static JsonElement Object(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static Task WriteErrorAsync(HttpContext http, int status, int code, string message)
    {
        http.Response.StatusCode = status;
        PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with { Output = JsonSerializer.SerializeToElement(new { code, message }) });
        return http.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = (string?)null, error = new { code, message } }, http.RequestAborted);
    }
}
