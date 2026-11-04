namespace IpLocation.Api.Common;

/// <summary>公开编码与历史存储编码分开；名称变更不搬迁既有版本、活动指针、启停或日志。</summary>
public static class ProviderCodes
{
    public const string Cz88 = "CZ88";
    public const string LegacyChunzhen = "chunzhen";

    public static string Public(string id) => id.Equals(LegacyChunzhen, StringComparison.OrdinalIgnoreCase) ||
        id.Equals(Cz88, StringComparison.OrdinalIgnoreCase) ? Cz88 : id;

    public static string Storage(string id) => Public(id) == Cz88 ? LegacyChunzhen : id;
    public static string EnabledSettingKey(string id) => $"provider.{Storage(id)}.enabled";
    public static string[] LogAliases(string id) => Public(id) == Cz88 ? [Cz88, LegacyChunzhen, "cz88"] : [id];
}
