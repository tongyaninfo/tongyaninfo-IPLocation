namespace IpLocation.Api.Options;

/// <summary>纯真 QQWry DAT 只读配置；路径留空，不自动搜索个人目录。</summary>
public sealed class ChunzhenOptions
{
    public bool Enabled { get; set; }
    public string DatabasePath { get; set; } = "";
    public long MaxDatabaseBytes { get; set; } = 268_435_456;
    public int MaxIndexEntries { get; set; } = 10_000_000;
}
