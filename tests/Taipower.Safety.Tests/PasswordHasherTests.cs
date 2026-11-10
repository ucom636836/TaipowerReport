using System.Security.Cryptography;
using System.Text;
using Taipower.Safety.Core.Security;
using Xunit;

namespace Taipower.Safety.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hash = PasswordHasher.Hash("Secret123!");
        Assert.Equal(PasswordVerification.Success, PasswordHasher.Verify("Secret123!", hash));
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("Secret123!");
        Assert.Equal(PasswordVerification.Failed, PasswordHasher.Verify("secret123!", hash));
    }

    [Fact]
    public void Hashes_are_salted_and_differ()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Fact]
    public void Legacy_sha512_big5_hash_verifies_and_requests_rehash()
    {
        // Reproduce the 2020 algorithm: Base64(SHA512(Big5(password + salt))).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string password = "pw測試123";
        const string salt = "17096";
        var legacyHash = Convert.ToBase64String(SHA512.HashData(Encoding.GetEncoding(950).GetBytes(password + salt)));
        var stored = PasswordHasher.FromLegacy(salt, legacyHash);

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, PasswordHasher.Verify(password, stored));
        Assert.Equal(PasswordVerification.Failed, PasswordHasher.Verify("wrong", stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-format")]
    [InlineData("pbkdf2$abc$def")]
    public void Verify_handles_malformed_stored_values(string stored)
    {
        Assert.Equal(PasswordVerification.Failed, PasswordHasher.Verify("whatever", stored));
    }
}
