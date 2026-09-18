using DicomSCP.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DicomSCP.Tests;

public class LoginAttemptLimiterTests
{
    private static LoginAttemptLimiter CreateLimiter(
        int maxAttempts = 3,
        int maxAttemptsPerIp = 10,
        int lockoutMinutes = 15)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:MaxLoginAttempts"] = maxAttempts.ToString(),
                ["Auth:MaxLoginAttemptsPerIp"] = maxAttemptsPerIp.ToString(),
                ["Auth:LockoutMinutes"] = lockoutMinutes.ToString()
            })
            .Build();
        return new LoginAttemptLimiter(config);
    }

    [Fact]
    public void BelowThreshold_IsNotLocked()
    {
        var limiter = CreateLimiter();

        limiter.RegisterFailure("u:alice", "ip:1.1.1.1");
        limiter.RegisterFailure("u:alice", "ip:1.1.1.1");

        Assert.False(limiter.IsLocked("u:alice", out _));
    }

    [Fact]
    public void AtThreshold_UserIsLocked_WithRetryAfter()
    {
        var limiter = CreateLimiter(maxAttempts: 3);

        for (var i = 0; i < 3; i++)
        {
            limiter.RegisterFailure("u:alice", "ip:1.1.1.1");
        }

        Assert.True(limiter.IsLocked("u:alice", out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Reset_ClearsFailures()
    {
        var limiter = CreateLimiter(maxAttempts: 3);

        limiter.RegisterFailure("u:alice", "ip:1.1.1.1");
        limiter.RegisterFailure("u:alice", "ip:1.1.1.1");
        limiter.Reset("u:alice");

        Assert.False(limiter.IsLocked("u:alice", out _));
    }

    [Fact]
    public void IpDimension_AccumulatesAcrossUsernames()
    {
        var limiter = CreateLimiter(maxAttempts: 3, maxAttemptsPerIp: 3);

        limiter.RegisterFailure("u:alice", "ip:9.9.9.9");
        limiter.RegisterFailure("u:bob", "ip:9.9.9.9");
        limiter.RegisterFailure("u:carol", "ip:9.9.9.9");

        Assert.False(limiter.IsLocked("u:alice", out _));
        Assert.True(limiter.IsLocked("ip:9.9.9.9", out _));
    }

    [Fact]
    public void UserKey_NormalizesCaseAndWhitespace()
    {
        Assert.Equal("u:admin", LoginAttemptLimiter.UserKey("  Admin "));
    }
}
