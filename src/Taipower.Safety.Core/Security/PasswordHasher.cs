using System.Security.Cryptography;
using System.Text;

namespace Taipower.Safety.Core.Security;

public enum PasswordVerification
{
    Failed,
    Success,
    /// <summary>Correct password stored with the legacy algorithm; caller should rehash.</summary>
    SuccessRehashNeeded,
}

/// <summary>
/// PBKDF2-SHA256 password hashing. Also verifies the legacy 2020 format
/// (Base64(SHA512(Big5(password + salt)))) so migrated accounts can log in once
/// and be upgraded transparently.
/// </summary>
public static class PasswordHasher
{
    private const string Pbkdf2Prefix = "pbkdf2";
    public const string LegacyPrefix = "legacy-sha512";
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    static PasswordHasher()
    {
        // Legacy code used Encoding.Default, i.e. code page 950 (Big5) on zh-TW Windows.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Pbkdf2Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Builds the stored value for an account migrated from the legacy Member table.</summary>
    public static string FromLegacy(string salt, string legacyHash) => $"{LegacyPrefix}${salt}${legacyHash}";

    public static PasswordVerification Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return PasswordVerification.Failed;
        var parts = stored.Split('$');

        if (parts.Length == 4 && parts[0] == Pbkdf2Prefix && int.TryParse(parts[1], out var iter))
        {
            byte[] salt, expected;
            try
            {
                salt = Convert.FromBase64String(parts[2]);
                expected = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException) { return PasswordVerification.Failed; }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return PasswordVerification.Failed;
            return iter < Iterations ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
        }

        if (parts.Length == 3 && parts[0] == LegacyPrefix)
        {
            var big5 = Encoding.GetEncoding(950);
            var actual = SHA512.HashData(big5.GetBytes(password + parts[1]));
            byte[] expected;
            try { expected = Convert.FromBase64String(parts[2]); }
            catch (FormatException) { return PasswordVerification.Failed; }
            return CryptographicOperations.FixedTimeEquals(actual, expected)
                ? PasswordVerification.SuccessRehashNeeded
                : PasswordVerification.Failed;
        }

        return PasswordVerification.Failed;
    }
}
