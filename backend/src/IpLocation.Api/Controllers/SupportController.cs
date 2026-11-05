using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using IpLocation.Api.Common;
using Tongyan.Support.Client.Models;
using Tongyan.Support.Client.Service;

namespace IpLocation.Api.Controllers;

/// <summary>用户主动反馈只转发白名单字段；凭据保留在服务端，回执仅从请求体读取。</summary>
[ApiController, Route("api/support"), AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SupportController(ProductSupportClient support) : ControllerBase
{
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);

    [HttpPost("feedback"), EnableRateLimiting("support-feedback"), RequestSizeLimit(32_768)]
    public async Task<IActionResult> Submit(FeedbackInput input, CancellationToken ct)
    {
        if (!SameOrigin()) return StatusCode(403, ApiResult<object>.Fail("FEEDBACK_ORIGIN_INVALID", "反馈请求来源无效。"));
        var response = await support.SubmitAsync(input, ct);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost("feedback/attachments/{clientAttachmentId:guid}"), EnableRateLimiting("support-feedback")]
    [RequestSizeLimit(10 * 1024 * 1024 + 65_536)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024 + 65_536, MemoryBufferThreshold = 10 * 1024 * 1024 + 65_536)]
    public async Task<IActionResult> Upload(Guid clientAttachmentId, CancellationToken ct)
    {
        if (!SameOrigin()) return StatusCode(403, ApiResult<object>.Fail("FEEDBACK_ORIGIN_INVALID", "反馈请求来源无效。"));
        if (!await UploadSlots.WaitAsync(0, ct)) return StatusCode(429, ApiResult<object>.Fail("FEEDBACK_UPLOAD_BUSY", "附件上传繁忙，请稍后重试。"));
        try
        {
            if (!Request.HasFormContentType) return BadRequest(ApiResult<object>.Fail("FEEDBACK_ATTACHMENT_INVALID", "请上传一个图片或 WAV 录音。"));
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (form.Files.Count != 1 || file is null || file.Length <= 0 || file.Length > ProductSupportClient.MaximumImageBytes ||
                file.ContentType is not ("image/png" or "image/jpeg" or "audio/wav") ||
                (file.ContentType == "audio/wav" && file.Length > ProductSupportClient.MaximumAudioBytes))
                return BadRequest(ApiResult<object>.Fail("FEEDBACK_ATTACHMENT_INVALID", "仅支持不超过10MiB的PNG/JPEG或不超过8MiB的WAV录音。"));
            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            var response = await support.UploadAsync(clientAttachmentId, file.ContentType, buffer.ToArray(), ct);
            return StatusCode(response.StatusCode, response.Body);
        }
        catch (InvalidDataException) { return BadRequest(ApiResult<object>.Fail("FEEDBACK_ATTACHMENT_INVALID", "附件格式或大小无效。")); }
        finally { UploadSlots.Release(); }
    }

    [HttpPost("feedback/{feedbackId}/lookup"), EnableRateLimiting("support-feedback"), RequestSizeLimit(4096)]
    public async Task<IActionResult> Lookup(string feedbackId, ReceiptInput input, CancellationToken ct)
    {
        var response = await support.LookupAsync(feedbackId, input, ct);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost("feedback/by-request/{clientRequestId:guid}/lookup"), EnableRateLimiting("support-feedback"), RequestSizeLimit(4096)]
    public async Task<IActionResult> LookupByRequest(Guid clientRequestId, ReceiptInput input, CancellationToken ct)
    {
        var response = await support.LookupByRequestAsync(clientRequestId, input, ct);
        return StatusCode(response.StatusCode, response.Body);
    }

    [HttpPost("feedback/{feedbackId}/attachments/{attachmentId:guid}/lookup"), EnableRateLimiting("support-feedback"), RequestSizeLimit(4096)]
    public async Task<IActionResult> Attachment(string feedbackId, Guid attachmentId, ReceiptInput input, CancellationToken ct)
    {
        var response = await support.DownloadAsync(feedbackId, attachmentId, input, ct);
        return response.Error is { } error ? StatusCode(response.StatusCode, error) : File(response.Bytes, response.ContentType);
    }

    private bool SameOrigin()
    {
        if (Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site") return false;
        var origin = Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) || string.Equals(origin, Request.Scheme + "://" + Request.Host, StringComparison.OrdinalIgnoreCase);
    }

}

