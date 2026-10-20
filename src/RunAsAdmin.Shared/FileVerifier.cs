using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RunAsAdmin.Shared;

public readonly record struct VerificationResult(bool Ok, string Detail);

/// <summary>
/// Prüft vor dem erhöhten Start, dass die Datei wirklich die erwartete ist:
/// per Authenticode-Herausgeber und/oder SHA-256. So kann ein ausgetauschtes
/// Programm nicht mit Adminrechten gestartet werden.
/// </summary>
public static class FileVerifier
{
    public static VerificationResult Verify(AllowedApp app)
    {
        if (string.IsNullOrWhiteSpace(app.Path))
            return new(false, "Kein Pfad angegeben.");
        if (!File.Exists(app.Path))
            return new(false, $"Datei existiert nicht: {app.Path}");

        // Wenn weder Herausgeber noch Hash gesetzt sind, ist die Datei nur über
        // ihren (admin-geschützten) Pfad abgesichert. Das protokollieren wir.
        var noIntegrityCheck = string.IsNullOrWhiteSpace(app.ExpectedPublisher)
                               && string.IsNullOrWhiteSpace(app.ExpectedSha256);
        if (noIntegrityCheck)
            return new(true, "Nur Pfadprüfung (kein Herausgeber/Hash hinterlegt).");

        if (!string.IsNullOrWhiteSpace(app.ExpectedSha256))
        {
            var actual = Sha256(app.Path);
            if (!string.Equals(actual, app.ExpectedSha256.Replace(":", "").Trim(),
                    StringComparison.OrdinalIgnoreCase))
                return new(false, $"SHA-256 passt nicht (erwartet {app.ExpectedSha256}, ist {actual}).");
        }

        if (!string.IsNullOrWhiteSpace(app.ExpectedPublisher))
        {
            var (ok, publisher) = AuthenticodePublisher(app.Path);
            if (!ok)
                return new(false, "Datei ist nicht gültig signiert.");
            if (!publisher.Contains(app.ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
                return new(false, $"Herausgeber passt nicht (erwartet '{app.ExpectedPublisher}', ist '{publisher}').");
        }

        return new(true, "Integritätsprüfung bestanden.");
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>
    /// Liest den Herausgeber aus der Authenticode-Signatur. Gibt (false, "") zurück,
    /// wenn die Datei nicht signiert ist oder die Signaturkette ungültig ist.
    /// </summary>
    public static (bool Ok, string Publisher) AuthenticodePublisher(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            var valid = chain.Build(cert);
            // Subject enthält z. B. CN="Valve Corp.", O=...
            return (valid, cert.Subject);
        }
        catch
        {
            return (false, "");
        }
    }
}
