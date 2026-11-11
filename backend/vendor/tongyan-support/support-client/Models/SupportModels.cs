using System.Text.Json;

namespace Tongyan.Support.Client.Models;

/// <summary>公开产品信息由宿主注入；不读取任何业务数据。</summary>
public sealed record SupportOptions(string Product, string ProductVersion, string StateDirectory, string Distribution,
    Uri Endpoint);

/// <summary>安装凭据只保存在受限本地文件，不返回浏览器。</summary>
public sealed record InstallationState(string InstallationId, string InstallationSecret, bool Registered);

/// <summary>提交快照的请求号与回执由调用方在第一次联网前持久保存。</summary>
public sealed record FeedbackInput(Guid ClientRequestId, string ReceiptToken, string Category,
    string? Title, string? Description, Guid[] AttachmentIds);

/// <summary>回执只通过请求体传递，禁止进入访问 URL。</summary>
public sealed record ReceiptInput(string ReceiptToken);

/// <summary>保留中央实际 HTTP 状态与统一业务信封。</summary>
public sealed record SupportResponse(int StatusCode, JsonElement Body)
{
    public static SupportResponse Failure(int status, string code, string message) => new(status,
        JsonSerializer.SerializeToElement(new { success = false, code, message, data = (object?)null }));
}

public sealed record SupportMediaResponse(int StatusCode, byte[] Bytes, string ContentType, JsonElement? Error = null);
