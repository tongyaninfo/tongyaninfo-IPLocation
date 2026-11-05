using System.Security.Cryptography;
using System.Text;

namespace Tongyan.Support.Client.Common;

/// <summary>签名绑定产品、安装身份、方法、路径及原始正文；不把签名或秘密写入日志。</summary>
public static class InstallationSignature
{
    public static string Sign(string method, string path, string product, string installationId,
        string timestamp, string nonce, byte[] body, string secret)
    {
        var canonical = string.Join('\n', "TY-INSTALL-HMAC-SHA256", method.ToUpperInvariant(), path, product,
            installationId, timestamp, nonce, Convert.ToHexStringLower(SHA256.HashData(body)));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromHexString(secret), Encoding.UTF8.GetBytes(canonical)));
    }
}
