using System.Security.Cryptography;
using System.Text;
using DicomSCP.Repository;
using Xunit;

namespace DicomSCP.Tests;

/// <summary>
/// PasswordHasher 单元测试：PBKDF2 格式、历史 SHA-256 兼容与升级标记。
/// </summary>
public class PasswordHasherTests
{
    private const int FastIterations = 1000;

    [Fact]
    public void Hash_Then_Verify_Roundtrip_Succeeds()
    {
        var stored = PasswordHasher.Hash("S3cure-Pass!", FastIterations);

        var ok = PasswordHasher.Verify("S3cure-Pass!", stored, out var needsUpgrade);

        Assert.True(ok);
        Assert.False(needsUpgrade);
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var stored = PasswordHasher.Hash("correct-horse", FastIterations);

        Assert.False(PasswordHasher.Verify("wrong-battery", stored, out _));
    }

    [Fact]
    public void Hash_SamePassword_Twice_DifferentSaltAndHash()
    {
        var a = PasswordHasher.Hash("same-password", FastIterations);
        var b = PasswordHasher.Hash("same-password", FastIterations);

        Assert.NotEqual(a, b);

        var saltA = a.Split('$')[2];
        var saltB = b.Split('$')[2];
        Assert.NotEqual(saltA, saltB);

        // 两个哈希都能验证同一口令
        Assert.True(PasswordHasher.Verify("same-password", a, out _));
        Assert.True(PasswordHasher.Verify("same-password", b, out _));
    }

    [Fact]
    public void Hash_UsesConfiguredIterations_EncodedInFormat()
    {
        var stored = PasswordHasher.Hash("pw", 123_456);

        var parts = stored.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("PBKDF2", parts[0]);
        Assert.Equal("123456", parts[1]);
    }

    [Fact]
    public void Verify_TamperedStoredHash_Fails()
    {
        var stored = PasswordHasher.Hash("pw", FastIterations);
        var parts = stored.Split('$');
        var hash = Convert.FromBase64String(parts[3]);
        hash[0] ^= 0xFF;
        var tampered = $"{parts[0]}${parts[1]}${parts[2]}${Convert.ToBase64String(hash)}";

        Assert.False(PasswordHasher.Verify("pw", tampered, out _));
    }

    [Theory]
    [InlineData("not-a-valid-format")]
    [InlineData("PBKDF2$abc$c2FsdA==$aGFzaA==")]      // 迭代次数非数字
    [InlineData("PBKDF2$0$c2FsdA==$aGFzaA==")]         // 迭代次数为 0
    [InlineData("PBKDF2$1000$!!!not-base64!!!$aGFzaA==")] // 盐非法 Base64
    public void Verify_MalformedStoredValue_Fails(string stored)
    {
        Assert.False(PasswordHasher.Verify("pw", stored, out _));
    }

    [Fact]
    public void Verify_LegacySha256Format_Verifies_AndFlagsUpgrade()
    {
        // 历史格式：无盐 SHA-256（Base64）——旧库中的实际存储形态
        var legacy = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("admin")));

        var ok = PasswordHasher.Verify("admin", legacy, out var needsUpgrade);

        Assert.True(ok);
        Assert.True(needsUpgrade);
    }

    [Fact]
    public void Verify_LegacySha256Format_WrongPassword_Fails()
    {
        var legacy = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("admin")));

        var ok = PasswordHasher.Verify("not-admin", legacy, out var needsUpgrade);

        Assert.False(ok);
        Assert.True(needsUpgrade); // 即使口令错误也识别为历史格式
    }

    [Fact]
    public void Hash_EmptyPassword_Throws()
    {
        Assert.Throws<ArgumentException>(() => PasswordHasher.Hash(""));
    }

    [Fact]
    public void Verify_NullOrEmptyInputs_Fail()
    {
        Assert.False(PasswordHasher.Verify(null, "stored", out _));
        Assert.False(PasswordHasher.Verify("pw", null, out _));
        Assert.False(PasswordHasher.Verify("pw", "", out _));
    }
}
