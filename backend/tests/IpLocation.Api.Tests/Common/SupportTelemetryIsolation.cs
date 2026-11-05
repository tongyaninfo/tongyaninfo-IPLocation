using System.Runtime.CompilerServices;

namespace IpLocation.Api.Tests.Common;

/// <summary>
/// 测试进程内强制关闭产品心跳，避免运行测试时向官网注册真实安装或污染遥测统计。
/// 只影响测试进程的环境变量；产品运行时默认值（<c>Support:TelemetryEnabled=true</c>）保持不变。
/// </summary>
internal static class SupportTelemetryIsolation
{
    [ModuleInitializer]
    internal static void Initialize() => Environment.SetEnvironmentVariable("Support__TelemetryEnabled", "false");
}
