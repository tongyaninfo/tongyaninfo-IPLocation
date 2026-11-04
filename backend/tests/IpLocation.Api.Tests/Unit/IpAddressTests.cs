using System.Net;
using IpLocation.Api.Common;

namespace IpLocation.Api.Tests.Unit;

/// <summary>外部输入必须是单个无歧义地址，不能借 .NET 的兼容解析混入其他格式。</summary>
public sealed class IpAddressTests
{
    [Theory]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0177.0.0.1")]
    [InlineData("1.1.1.1:80")]
    [InlineData("https://1.1.1.1")]
    [InlineData("1.1.1.0/24")]
    [InlineData("1.1.1.1\n")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fe80::1%en0")]
    [InlineData("example.com")]
    public void RejectsAmbiguousOrNonIpInput(string value) => Assert.False(IpAddressParser.TryParse(value, out _));

    [Fact]
    public void MapsIpv4MappedIpv6ToSameIpv4Identity()
    {
        Assert.True(IpAddressParser.TryParse("::ffff:10.20.30.40", out var address));
        Assert.Equal("10.20.30.40", address.ToString());
        Assert.Equal("private", IpAddressParser.Classify(address));
    }

    [Theory]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("10.20.30.40", "private")]
    [InlineData("fd12:3456::1", "private")]
    [InlineData("169.254.1.2", "linkLocal")]
    [InlineData("fe80::1", "linkLocal")]
    [InlineData("198.51.100.5", "documentation")]
    [InlineData("2001:db8::1", "documentation")]
    [InlineData("1.1.1.1", "public")]
    [InlineData("2001:4860:4860::8888", "public")]
    public void DistinguishesSpecialAddressFromPublicLocation(string value, string classification) =>
        Assert.Equal(classification, IpAddressParser.Classify(IPAddress.Parse(value)));
}
