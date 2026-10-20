using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RunAsAdmin.Shared;

/// <summary>
/// Verschlüsselt Geheimnisse mit Windows DPAPI (LocalMachine-Scope), damit der
/// als SYSTEM laufende Dienst sie wieder lesen kann. Werte liegen nie im Klartext
/// in der config. PINs werden als gesalzener PBKDF2-Hash gespeichert.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RunAsAdmin.v1");

    /// <summary>Verschlüsselt Klartext -> Base64 (oder null bei leer).</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy,
            DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(data);
    }

    /// <summary>Entschlüsselt Base64 -> Klartext (leer bei null/Fehler).</summary>
    public static string Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(enc), Entropy,
                DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(data);
        }
        catch { return ""; }
    }

    // --- PIN (gesalzener PBKDF2-Hash) ---

    public static string HashPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt,
            100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    public static bool VerifyPin(string pin, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.Contains(':')) return false;
        var parts = stored.Split(':', 2);
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt,
                100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
