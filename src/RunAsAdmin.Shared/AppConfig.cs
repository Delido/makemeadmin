using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunAsAdmin.Shared;

/// <summary>
/// Gesamte Konfiguration des Dienstes. Wird aus einer JSON-Datei geladen, die nur
/// von Administratoren/SYSTEM beschreibbar sein darf (siehe Installer).
/// </summary>
public sealed class AppConfig
{
    /// <summary>Freigegebene Programme, die der Dienst erhöht starten darf.</summary>
    public List<AllowedApp> AllowedApps { get; set; } = new();

    /// <summary>
    /// Freigabe-Modus: HomeAssistant (Webhooks, Rückkanal) oder Server (HA-Add-on mit Polling).
    /// </summary>
    public ApprovalMode Mode { get; set; } = ApprovalMode.HomeAssistant;

    /// <summary>Anbindung an Home Assistant für die Freigabe unbekannter Programme.</summary>
    public HomeAssistantConfig HomeAssistant { get; set; } = new();

    /// <summary>Anbindung an das RunAsAdmin-Server-Add-on (Modus „Server").</summary>
    public ServerConfig Server { get; set; } = new();

    /// <summary>Verzeichnis für die Protokolldateien.</summary>
    public string LogDirectory { get; set; } = @"C:\ProgramData\RunAsAdmin\logs";

    /// <summary>
    /// Named Pipe, über die das Kinder-Tool mit dem Dienst spricht.
    /// </summary>
    public string PipeName { get; set; } = "RunAsAdmin";

    /// <summary>
    /// Wie lange (Sekunden) auf eine Freigabe aus Home Assistant gewartet wird,
    /// bevor die Anfrage als abgelehnt gilt.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gesalzener Hash der Eltern-PIN, die den Einstellungsbereich schützt.
    /// null = noch keine PIN gesetzt (erste Einrichtung).
    /// </summary>
    public string? ParentPinHash { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Pfad, aus dem geladen wurde (für spätere Speicherungen). Nicht serialisiert.</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    private readonly object _saveGate = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Konfigurationsdatei nicht gefunden: {path}");

        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
                  ?? throw new InvalidDataException($"Konfiguration leer oder ungültig: {path}");
        cfg.SourcePath = path;
        return cfg;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        try
        {
            File.WriteAllText(path, json);
        }
        catch (UnauthorizedAccessException)
        {
            // Der Dienst laeuft als SYSTEM und darf seine eigene Konfiguration immer
            // schreiben. Falls eine zu strenge ACL das verhindert: Attribute loesen
            // und ein zweites Mal versuchen (bewusst einfach gehalten, kein ACL-Umbau).
            try
            {
                if (File.Exists(path))
                    File.SetAttributes(path, FileAttributes.Normal);
            }
            catch { }
            File.WriteAllText(path, json);
        }
    }

    /// <summary>
    /// Trägt ein dauerhaft erlaubtes Programm ein und speichert die Datei.
    /// Ersetzt einen vorhandenen Eintrag mit gleichem Pfad. Threadsicher.
    /// </summary>
    public void AddAllowedApp(AllowedApp app)
    {
        lock (_saveGate)
        {
            AllowedApps.RemoveAll(a =>
                string.Equals(a.Path, app.Path, StringComparison.OrdinalIgnoreCase));
            AllowedApps.Add(app);
            if (!string.IsNullOrEmpty(SourcePath)) Save(SourcePath);
        }
    }

    /// <summary>
    /// Entfernt ein freigegebenes Programm (per Pfad) und speichert. Threadsicher.
    /// Gibt true zurück, wenn etwas entfernt wurde.
    /// </summary>
    public bool RemoveAllowedApp(string path)
    {
        lock (_saveGate)
        {
            var removed = AllowedApps.RemoveAll(a =>
                string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0 && !string.IsNullOrEmpty(SourcePath)) Save(SourcePath);
            return removed > 0;
        }
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    // Kompakt (einzeilig) – für das zeilenbasierte Pipe-/HTTP-Protokoll.
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SerializeCompact(object value) => JsonSerializer.Serialize(value, CompactOptions);
}

/// <summary>Grobe Art eines freigegebenen Programms – nur für Anzeige/Gruppierung.</summary>
public enum AppCategory
{
    Spiel,
    Tool,
    Sonstiges,
}

/// <summary>Regel für ein freigegebenes Programm.</summary>
public sealed class AllowedApp
{
    /// <summary>Anzeigename im Kinder-Tool, z. B. "Steam" oder "SignalRGB".</summary>
    public string Name { get; set; } = "";

    /// <summary>Vollständiger Pfad zur ausführbaren Datei.</summary>
    public string Path { get; set; } = "";

    /// <summary>Art des Programms (nur Anzeige): Spiel, Tool oder Sonstiges.</summary>
    public AppCategory Category { get; set; } = AppCategory.Sonstiges;

    /// <summary>Optionale Startargumente.</summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Bei true startet das Programm sofort erhöht und wird nur protokolliert.
    /// Bei false muss die Freigabe über Home Assistant erfolgen.
    /// </summary>
    public bool AutoApprove { get; set; }

    /// <summary>
    /// Erwarteter Name des Herausgebers aus der Code-Signatur (Authenticode).
    /// Ist er gesetzt, startet der Dienst die Datei nur, wenn die Signatur gültig
    /// ist und der Herausgeber passt. Schutz gegen ausgetauschte Dateien.
    /// </summary>
    public string? ExpectedPublisher { get; set; }

    /// <summary>
    /// Alternativ zum Herausgeber: erwarteter SHA-256-Hash der Datei (Hex, ohne Doppelpunkte).
    /// Für unsignierte Programme.
    /// </summary>
    public string? ExpectedSha256 { get; set; }

    /// <summary>Freitext, warum das Programm erhöht laufen muss (fürs Protokoll).</summary>
    public string? Reason { get; set; }
}

public sealed class HomeAssistantConfig
{
    /// <summary>Basis-URL der Home-Assistant-Instanz (ohne abschließenden Slash).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Webhook-ID in Home Assistant, an die Anfragen gehen.</summary>
    public string RequestWebhookId { get; set; } = "runasadmin_request";

    /// <summary>Webhook-ID für Protokoll-Ereignisse (Logbuch/Statistik in HA).</summary>
    public string EventWebhookId { get; set; } = "runasadmin_event";

    /// <summary>Protokoll-Ereignisse zusätzlich an Home Assistant melden.</summary>
    public bool ReportEvents { get; set; } = true;

    /// <summary>
    /// Shared Secret für den Rückkanal – DPAPI-verschlüsselt (LocalMachine).
    /// Wird nie im Klartext gespeichert. Zugriff über <see cref="GetSecret"/>.
    /// </summary>
    public string? CallbackSecretEnc { get; set; }

    /// <summary>
    /// Port, auf dem der Dienst lokal auf die Entscheidung von Home Assistant lauscht.
    /// </summary>
    public int CallbackPort { get; set; } = 8731;

    public bool Enabled { get; set; } = false;

    /// <summary>Entschlüsseltes Secret (leer, wenn nicht gesetzt). Nicht serialisiert.</summary>
    [JsonIgnore]
    public string CallbackSecret => Secret.Unprotect(CallbackSecretEnc);

    /// <summary>Setzt das Secret (verschlüsselt es sofort).</summary>
    public void SetSecret(string plain) => CallbackSecretEnc = Secret.Protect(plain);
}

/// <summary>Wie die Freigabe eingeholt wird.</summary>
public enum ApprovalMode
{
    /// <summary>Direkt per Home-Assistant-Webhooks mit lokalem Rückkanal-Listener.</summary>
    HomeAssistant,

    /// <summary>Über das RunAsAdmin-Server-Add-on: Anfrage senden, Entscheidung pollen.</summary>
    Server,
}

/// <summary>
/// Anbindung an das RunAsAdmin-Server-Add-on (FastAPI). Der Dienst schickt die
/// Anfrage an das Add-on und pollt die Entscheidung – kein offener Port am PC nötig.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Basis-URL des Add-ons, z. B. http://192.168.178.10:8099 (ohne Slash am Ende).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Gemeinsames Token (muss der Add-on-Option <c>pc_token</c> entsprechen) –
    /// DPAPI-verschlüsselt gespeichert. Zugriff über <see cref="Token"/>.
    /// </summary>
    public string? TokenEnc { get; set; }

    /// <summary>Protokoll-Ereignisse zusätzlich an das Add-on melden (Dashboard/Verlauf).</summary>
    public bool ReportEvents { get; set; } = true;

    /// <summary>Abstand zwischen zwei Status-Abfragen während des Wartens (Sekunden).</summary>
    public int PollSeconds { get; set; } = 2;

    /// <summary>Entschlüsseltes Token (leer, wenn nicht gesetzt). Nicht serialisiert.</summary>
    [JsonIgnore]
    public string Token => Secret.Unprotect(TokenEnc);

    /// <summary>Setzt das Token (verschlüsselt es sofort).</summary>
    public void SetToken(string plain) => TokenEnc = Secret.Protect(plain);
}
