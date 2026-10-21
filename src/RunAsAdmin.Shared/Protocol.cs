namespace RunAsAdmin.Shared;

/// <summary>Art der Pipe-Nachricht vom Kinder-Tool an den Dienst.</summary>
public enum PipeVerb
{
    /// <summary>Freigegebene, anzeigbare Programme abfragen (für die GUI).</summary>
    List = 0,
    /// <summary>Start eines Programms anfordern.</summary>
    Run = 1,
    /// <summary>Volle Adminrechte für einen festen Zeitraum anfordern (nur mit Freigabe).</summary>
    GrantTime = 2,
    /// <summary>Status einer laufenden Admin-Zeit abfragen (Restzeit).</summary>
    TimeStatus = 3,
    /// <summary>Ein heruntergeladenes Update-ZIP einspielen (Dienst als SYSTEM).</summary>
    ApplyUpdate = 4,
    /// <summary>Aktuelle Einstellungen abfragen (ohne Secret-Klartext).</summary>
    GetSettings = 5,
    /// <summary>Einstellungen speichern (nur nach PIN-Prüfung; Dienst schreibt als SYSTEM).</summary>
    SaveSettings = 6,
    /// <summary>Prüfen, ob schon eine Eltern-PIN gesetzt ist.</summary>
    PinStatus = 7,
    /// <summary>Verlauf der letzten Ereignisse abfragen (für die GUI-Anzeige).</summary>
    History = 8,
    /// <summary>Prüfen, ob das Kind-Konto dauerhaft Admin ist (Warnung).</summary>
    AdminCheck = 9,
    /// <summary>Kind-Konto auf Standardbenutzer herunterstufen (nur mit PIN).</summary>
    Demote = 10,
}

/// <summary>Antwort auf AdminCheck / Demote.</summary>
public sealed class AdminCheckResponse
{
    public RequestState State { get; set; }
    public string Message { get; set; } = "";
    /// <summary>true = Kind-Konto ist dauerhaft Admin (Tool wirkungslos!).</summary>
    public bool IsPermanentAdmin { get; set; }
    public string Account { get; set; } = "";
}

/// <summary>Ein Verlaufseintrag für die GUI.</summary>
public sealed class HistoryItem
{
    public string Time { get; set; } = "";
    public string Text { get; set; } = "";
    public string Kind { get; set; } = ""; // info | grant | deny
}

public sealed class HistoryResponse
{
    public List<HistoryItem> Items { get; set; } = new();
}

/// <summary>Umschlag für alle Pipe-Nachrichten.</summary>
public sealed class PipeMessage
{
    public PipeVerb Verb { get; set; } = PipeVerb.Run;
    public ElevationRequest? Request { get; set; }
    /// <summary>Begründung des Kindes für eine Admin-Zeit (optional).</summary>
    public string? Reason { get; set; }
    /// <summary>Pfad zum heruntergeladenen Update-ZIP (für ApplyUpdate).</summary>
    public string? UpdateZipPath { get; set; }

    /// <summary>Für GetSettings/SaveSettings/PinStatus.</summary>
    public SettingsPayload? Settings { get; set; }
}

/// <summary>Einstellungen, die die GUI anzeigt/speichert. Enthält nie das Secret im Klartext beim Lesen.</summary>
public sealed class SettingsPayload
{
    // Eingabe/Anzeige
    public string BaseUrl { get; set; } = "";
    public string RequestWebhookId { get; set; } = "runasadmin_request";
    public string EventWebhookId { get; set; } = "runasadmin_event";
    public int CallbackPort { get; set; } = 8731;
    public bool Enabled { get; set; }
    public bool ReportEvents { get; set; } = true;

    /// <summary>true, wenn ein Secret gesetzt ist (ohne es preiszugeben).</summary>
    public bool SecretIsSet { get; set; }

    // --- Nur beim Speichern gesetzt ---
    /// <summary>PIN zur Autorisierung des Speicherns (Pflicht, außer beim ersten Setzen).</summary>
    public string? Pin { get; set; }
    /// <summary>Neues Secret im Klartext; null = unverändert lassen.</summary>
    public string? NewSecret { get; set; }
    /// <summary>Neue PIN setzen/ändern; null = unverändert.</summary>
    public string? NewPin { get; set; }
}

/// <summary>Antwort auf Settings-/PIN-Operationen.</summary>
public sealed class SettingsResponse
{
    public RequestState State { get; set; }
    public string Message { get; set; } = "";
    public bool PinIsSet { get; set; }
    public SettingsPayload? Settings { get; set; }
}

/// <summary>Antwort auf GrantTime / TimeStatus.</summary>
public sealed class TimeAdminResponse
{
    public RequestState State { get; set; }
    public string Message { get; set; } = "";
    public string Decision { get; set; } = "";
    /// <summary>Verbleibende Sekunden, falls eine Admin-Zeit aktiv ist.</summary>
    public int RemainingSeconds { get; set; }
    public bool Active { get; set; }
}

/// <summary>Ein für die GUI anzeigbares Programm (ohne interne Felder wie Secret).</summary>
public sealed class AppInfo
{
    public string Name { get; set; } = "";
    public bool AutoApprove { get; set; }
    public string? Reason { get; set; }
    /// <summary>true, wenn die Datei am hinterlegten Pfad existiert.</summary>
    public bool Available { get; set; }
}

/// <summary>Antwort auf eine List-Anfrage.</summary>
public sealed class AppListResponse
{
    public List<AppInfo> Apps { get; set; } = new();
}

/// <summary>Anfrage vom Kinder-Tool an den Dienst (über die Named Pipe).</summary>
public sealed class ElevationRequest
{
    /// <summary>Name der angeforderten App aus der Freigabeliste, falls bekannt.</summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Pfad einer sonst unbekannten Datei, die das Kind zur Freigabe vorschlägt.
    /// Wird nur verwendet, wenn AppName leer ist.
    /// </summary>
    public string? CustomPath { get; set; }

    /// <summary>Begründung des Kindes (optional, geht ins Protokoll und an HA).</summary>
    public string? Reason { get; set; }
}

public enum RequestState
{
    Denied = 0,
    Approved = 1,
    Error = 2,
}

/// <summary>Antwort des Dienstes an das Kinder-Tool.</summary>
public sealed class ElevationResponse
{
    public RequestState State { get; set; }
    public string Message { get; set; } = "";

    /// <summary>Wie die Entscheidung zustande kam: Auto, HomeAssistant, Timeout, Policy.</summary>
    public string Decision { get; set; } = "";
}

/// <summary>Nachricht, die der Dienst an Home Assistant schickt.</summary>
public sealed class ApprovalAsk
{
    public string RequestId { get; set; } = "";
    public string Computer { get; set; } = Environment.MachineName;
    public string User { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Reason { get; set; }

    /// <summary>Secret für den Rückkanal.</summary>
    public string Secret { get; set; } = "";

    /// <summary>URL, an die Home Assistant die Entscheidung zurückschicken soll.</summary>
    public string CallbackUrl { get; set; } = "";

    /// <summary>
    /// true, wenn dieses Programm noch nicht dauerhaft freigegeben ist und am Handy
    /// zusätzlich "Immer erlauben" angeboten werden soll.
    /// </summary>
    public bool AllowPersist { get; set; }
}

/// <summary>Entscheidung, die Home Assistant an den Dienst zurückschickt.</summary>
public sealed class ApprovalDecision
{
    public string RequestId { get; set; } = "";
    public bool Approved { get; set; }
    public string Secret { get; set; } = "";

    /// <summary>
    /// true, wenn "Immer erlauben" gewählt wurde: der Dienst trägt das Programm
    /// dauerhaft in die Freigabeliste ein (dieser Pfad, abgesichert per Hash/Herausgeber).
    /// </summary>
    public bool Persist { get; set; }

    /// <summary>Wer in HA entschieden hat (optional, fürs Protokoll).</summary>
    public string? DecidedBy { get; set; }
}
