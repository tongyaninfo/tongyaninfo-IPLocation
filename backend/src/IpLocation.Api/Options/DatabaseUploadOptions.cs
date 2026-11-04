namespace IpLocation.Api.Options;

/// <summary>上传只保存不可变原文件；目录必须在源码和 Web 根目录之外，不自动清理文件。</summary>
public sealed class DatabaseUploadOptions
{
    public string StorageRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Tongyan", "IpLocation", "databases");
    public long MaxFileBytes { get; set; } = 1_073_741_824;
    public long MaxTotalBytes { get; set; } = 4_294_967_296;
}
