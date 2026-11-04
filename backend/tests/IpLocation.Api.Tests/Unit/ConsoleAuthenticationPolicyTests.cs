using IpLocation.Api.Common;
using IpLocation.Api.Service.impl;

namespace IpLocation.Api.Tests.Unit;

public sealed class ConsoleAuthenticationPolicyTests
{
    [Fact]
    public void AccountRateLimitBlocksRepeatedAccountAcrossRequestOrigins()
    {
        using var limiter = new ConsoleLoginAttemptLimiter();
        for (var index = 0; index < 10; index++) Assert.True(limiter.TryAcquire("FIXTURE-ADMIN"));
        Assert.False(limiter.TryAcquire("FIXTURE-ADMIN"));
        Assert.True(limiter.TryAcquire("OTHER-FIXTURE-ADMIN"));
    }

    [Theory]
    [InlineData(" fixture-admin ", "FIXTURE-ADMIN")]
    [InlineData("FiXtUrE", "FIXTURE")]
    [InlineData(null, "")]
    public void AccountIdentityUsesSameNormalizationForInitializeAndLogin(string? account, string expected) =>
        Assert.Equal(expected, ConsoleIdentityService.NormalizeAccount(account));
}
