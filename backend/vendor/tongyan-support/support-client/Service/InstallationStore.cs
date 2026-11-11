using System.Security.Cryptography;
using System.Text.Json;
using Tongyan.Support.Client.Models;

namespace Tongyan.Support.Client.Service;

/// <summary>GUID 与秘密必须先共同落盘再注册；损坏、权限或磁盘故障绝不生成替代身份。</summary>
public sealed class InstallationStore(SupportOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    public string StateDirectory => options.StateDirectory;

    public Task<InstallationState> ReadAsync(CancellationToken ct) => UpdateAsync(state => state, ct);

    public async Task<InstallationState> UpdateAsync(Func<InstallationState, InstallationState> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireDirectory(options.StateDirectory);
            await using var lease = await LockAsync(Path.Combine(options.StateDirectory, "identity.lock"), ct);
            var path = Path.Combine(options.StateDirectory, "installation.json");
            InstallationState current;
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("身份文件不能使用符号链接。");
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) &
                    (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    throw new UnauthorizedAccessException("身份文件权限过宽，需由管理员核对原文件权限。");
                current = JsonSerializer.Deserialize<InstallationState>(await File.ReadAllBytesAsync(path, ct), Json)
                    ?? throw new IOException("身份文件不可读取。");
                if (!Guid.TryParseExact(current.InstallationId, "D", out var guid) || guid == Guid.Empty ||
                    current.InstallationId != guid.ToString("D") || current.InstallationSecret is not { Length: 64 } ||
                    current.InstallationSecret.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                    throw new IOException("身份文件内容无效。");
            }
            else current = new(Guid.NewGuid().ToString("D"), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), false);
            var next = change(current);
            if (!File.Exists(path) || next != current) await WriteAtomicAsync(path, JsonSerializer.SerializeToUtf8Bytes(next, Json), ct);
            return next;
        }
        finally { gate.Release(); }
    }

    public static void RequireDirectory(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new IOException("支持数据目录必须是绝对路径。");
            Directory.CreateDirectory(path);
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("支持数据目录不能包含符号链接。");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { throw new IOException("支持数据目录配置无效。", exception); }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("锁文件无效。");
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException) when (attempt < 30) { await Task.Delay(100, ct); }
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough | FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temporary, settings))
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
