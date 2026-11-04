using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace IpLocation.Api.Common;

/// <summary>HTTP JSON 共用编码：中文直接输出 UTF-8，仍转义 HTML 敏感字符与控制字符。</summary>
public static class ApiJsonEncoding
{
    // 只调整语言字符范围，不使用放宽 HTML 防护的 UnsafeRelaxedJsonEscaping。
    // 此配置仅供 HTTP 表示层使用，不改变持久化 JSON 或访问日志的容量计算。
    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.Create(UnicodeRanges.All);
}
