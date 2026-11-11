using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Tongyan.Support.Client.Common;
using Tongyan.Support.Client.Models;

namespace Tongyan.Support.Client.Service;

/// <summary>只发送显式白名单元数据及用户主动提交的反馈；网络与状态文件失败均隔离到支持功能。</summary>
public sealed class ProductSupportClient(SupportOptions options, HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly InstallationStore store = new(options);
    private readonly SemaphoreSlim registration = new(1, 1);
    public const int MaximumImageBytes = 10 * 1024 * 1024;
    public const int MaximumAudioBytes = 8 * 1024 * 1024;

    public Task<SupportResponse> SubmitAsync(FeedbackInput input, CancellationToken ct)
    {
        if (input.ClientRequestId == Guid.Empty || !ValidReceipt(input.ReceiptToken) ||
            input.Category is not ("bug" or "suggestion" or "question" or "other") || input.Title?.Length > 120 ||
            input.Description?.Length > 2000 || input.AttachmentIds is null || input.AttachmentIds.Length > 6 ||
            input.AttachmentIds.Any(id => id == Guid.Empty) || input.AttachmentIds.Distinct().Count() != input.AttachmentIds.Length ||
            (string.IsNullOrWhiteSpace(input.Description) && input.AttachmentIds.Length == 0))
            return Task.FromResult(SupportResponse.Failure(400, "FEEDBACK_INVALID", "请检查反馈文字、附件和回执。"));
        return SendJsonAsync("/api/feedback/v1/tickets", Serialize(new { input.ClientRequestId, input.ReceiptToken,
            input.Category, input.Title, input.Description, productVersion = options.ProductVersion, input.AttachmentIds }), ct);
    }

    public Task<SupportResponse> LookupAsync(string feedbackId, ReceiptInput input, CancellationToken ct) =>
        ValidId(feedbackId) && ValidReceipt(input.ReceiptToken)
            ? SendJsonAsync($"/api/feedback/v1/tickets/{feedbackId}/receipt", Serialize(input), ct)
            : Task.FromResult(SupportResponse.Failure(400, "FEEDBACK_RECEIPT_INVALID", "反馈编号或回执无效。"));

    public Task<SupportResponse> LookupByRequestAsync(Guid requestId, ReceiptInput input, CancellationToken ct) =>
        requestId != Guid.Empty && ValidReceipt(input.ReceiptToken)
            ? SendJsonAsync($"/api/feedback/v1/tickets/by-request/{requestId:D}/receipt", Serialize(input), ct)
            : Task.FromResult(SupportResponse.Failure(400, "FEEDBACK_RECEIPT_INVALID", "反馈请求号或回执无效。"));

    /// <summary>使用有界原始文件正文签名，中央再次核验文件魔数、图像尺寸与 WAV 时长。</summary>
    public async Task<SupportResponse> UploadAsync(Guid attachmentId, string contentType, byte[] bytes, CancellationToken ct)
    {
        if (attachmentId == Guid.Empty || bytes.Length == 0 || contentType is not ("image/png" or "image/jpeg" or "audio/wav") ||
            bytes.Length > (contentType == "audio/wav" ? MaximumAudioBytes : MaximumImageBytes))
            return SupportResponse.Failure(400, "FEEDBACK_ATTACHMENT_INVALID", "仅支持大小有界的 PNG、JPEG 和 WAV 附件。");
        return await SendAsync($"/api/feedback/v1/attachments/{attachmentId:D}", bytes, contentType, ct);
    }

    public async Task<SupportMediaResponse> DownloadAsync(string feedbackId, Guid attachmentId, ReceiptInput input, CancellationToken ct)
    {
        if (!ValidId(feedbackId) || attachmentId == Guid.Empty || !ValidReceipt(input.ReceiptToken))
            return new(400, [], "application/json", SupportResponse.Failure(400, "FEEDBACK_RECEIPT_INVALID", "反馈回执无效。").Body);
        try
        {
            var registered = await EnsureRegisteredAsync(ct);
            if (registered is not null) return new(registered.StatusCode, [], "application/json", registered.Body);
            var state = await store.ReadAsync(ct);
            var path = $"/api/feedback/v1/tickets/{feedbackId}/attachments/{attachmentId:D}/receipt";
            using var request = Signed(path, Serialize(input), "application/json", state);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await ReadBoundedAsync(response.Content, MaximumImageBytes + 1024, ct);
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, [], "application/json", Parse(bytes));
            if (type is not ("image/png" or "image/jpeg" or "audio/wav")) return new(502, [], "application/json", Failure().Body);
            return new((int)response.StatusCode, bytes, type);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException)
        { return new(503, [], "application/json", Failure().Body); }
    }

    private Task<SupportResponse> SendJsonAsync(string path, byte[] bytes, CancellationToken ct) =>
        SendAsync(path, bytes, "application/json", ct);

    private async Task<SupportResponse> SendAsync(string path, byte[] bytes, string contentType, CancellationToken ct)
    {
        try
        {
            var registered = await EnsureRegisteredAsync(ct);
            if (registered is not null) return registered;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var state = await store.ReadAsync(ct);
                using var request = Signed(path, bytes, contentType, state);
                try
                {
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    var body = Parse(await ReadBoundedAsync(response.Content, 256 * 1024, ct));
                    if (attempt == 0 && (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests))
                    {
                        // 仅在当前反馈请求内短时重试，限制 Retry-After 等待时间。
                        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                        if (delay > TimeSpan.FromSeconds(3)) return new((int)response.StatusCode, body);
                        await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
                        continue;
                    }
                    return new((int)response.StatusCode, body);
                }
                catch (Exception exception) when (attempt == 0 && exception is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                { await Task.Delay(500, ct); }
            }
            return Failure();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException)
        { return Failure(); }
    }

    private async Task<SupportResponse?> EnsureRegisteredAsync(CancellationToken ct)
    {
        await registration.WaitAsync(ct);
        try
        {
            var state = await store.ReadAsync(ct);
            if (state.Registered) return null;
            var body = Serialize(new { product = options.Product, state.InstallationId, state.InstallationSecret,
                productVersion = options.ProductVersion, os = Os(), arch = Arch(), distribution = options.Distribution });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint, "/api/products/v1/installations/register"));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var result = new SupportResponse((int)response.StatusCode, Parse(await ReadBoundedAsync(response.Content, 64 * 1024, ct)));
            if (!response.IsSuccessStatusCode || !result.Body.TryGetProperty("success", out var success) || !success.GetBoolean()) return result;
            await store.UpdateAsync(current => current with { Registered = true }, ct);
            return null;
        }
        finally { registration.Release(); }
    }

    private HttpRequestMessage Signed(string path, byte[] bytes, string contentType, InstallationState state)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Endpoint, path));
        request.Headers.Add("X-TY-Product", options.Product);
        request.Headers.Add("X-TY-Installation-Id", state.InstallationId);
        request.Headers.Add("X-TY-Timestamp", timestamp);
        request.Headers.Add("X-TY-Nonce", nonce);
        request.Headers.Add("X-TY-Signature", InstallationSignature.Sign("POST", path, options.Product, state.InstallationId,
            timestamp, nonce, bytes, state.InstallationSecret));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw new IOException("支持响应超出限制。");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        for (int read; (read = await stream.ReadAsync(block, ct)) > 0;)
        { if (buffer.Length + read > maximum) throw new IOException("支持响应超出限制。"); await buffer.WriteAsync(block.AsMemory(0, read), ct); }
        return buffer.ToArray();
    }

    private static JsonElement Parse(byte[] bytes)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(bytes);
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("success", out var success) ||
            success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !body.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
            !body.TryGetProperty("data", out _)) throw new JsonException("支持响应信封无效。");
        return body;
    }
    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    private static bool ValidReceipt(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
    private static bool ValidId(string value) => value.Length is > 0 and <= 24 && value.All(char.IsAsciiDigit);
    private static string Os() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
    private static string Arch() => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    private static SupportResponse Failure() => SupportResponse.Failure(503, "SUPPORT_UNAVAILABLE", "官网支持服务暂不可用，请保留原回执稍后核对。");
}
