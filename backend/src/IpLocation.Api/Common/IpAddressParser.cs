using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace IpLocation.Api.Common;

/// <summary>拒绝 .NET 兼容的短 IPv4、整数、八进制以及带作用域地址，保持外部合同无歧义。</summary>
public static class IpAddressParser
{
    public static bool TryParse(string? input, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(input) || input.Length > 45 || input.Any(char.IsWhiteSpace) ||
            input.IndexOfAny(['[', ']', '%', '/', '\\', '?', '#']) >= 0)
        {
            return false;
        }

        if (!input.Contains(':'))
        {
            var parts = input.Split('.');
            if (parts.Length != 4 || parts.Any(part => part.Length is < 1 or > 3 ||
                    (part.Length > 1 && part[0] == '0') || part.Any(ch => ch is < '0' or > '9') ||
                    !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(input, out var parsed))
        {
            return false;
        }

        address = Normalize(parsed);
        return true;
    }

    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    public static string Classify(IPAddress address)
    {
        address = Normalize(address);
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return "unspecified";
        if (IPAddress.IsLoopback(address)) return "loopback";

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168)) return "private";
            if (bytes[0] == 169 && bytes[1] == 254) return "linkLocal";
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return "shared";
            if ((bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)) return "documentation";
            if (bytes[0] == 198 && bytes[1] is 18 or 19) return "benchmark";
            if (bytes[0] is >= 224 and <= 239) return "multicast";
            if (bytes[0] == 0 || bytes[0] >= 240 ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)) return "reserved";
            return "public";
        }

        if (address.IsIPv6LinkLocal) return "linkLocal";
        if (address.IsIPv6Multicast) return "multicast";
        if (InNetwork(address, "fc00::/7")) return "private";
        if (InNetwork(address, "2001:db8::/32")) return "documentation";
        if (InNetwork(address, "2001:2::/48")) return "benchmark";
        if (InNetwork(address, "64:ff9b::/96") || InNetwork(address, "64:ff9b:1::/48")) return "translation";
        if (address.IsIPv6SiteLocal || InNetwork(address, "100::/64") || InNetwork(address, "::/96")) return "reserved";
        return InNetwork(address, "2000::/3") ? "public" : "reserved";
    }

    private static bool InNetwork(IPAddress address, string network) => IPNetwork.Parse(network).Contains(address);
}
