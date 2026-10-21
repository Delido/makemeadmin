using System.Runtime.Versioning;
using RunAsAdmin.Service.Approval;
using RunAsAdmin.Service.Elevation;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service;

/// <summary>
/// Entscheidet über eine Anfrage und startet das Programm bei Freigabe.
/// Ablauf: App in Freigabeliste finden → Datei verifizieren → bei AutoApprove
/// sofort starten, sonst Home Assistant fragen → starten oder ablehnen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RequestHandler
{
    private readonly AppConfig _cfg;
    private readonly AuditLog _log;
    private readonly HomeAssistantApprover _approver;
    private readonly Elevator _elevator = new();
    private readonly TimeAdminManager _timeAdmin;

    /// <summary>Feste Dauer der Admin-Zeit (laut Vorgabe 15 Minuten).</summary>
    public const int TimeAdminMinutes = 15;

    public RequestHandler(AppConfig cfg, AuditLog log, HomeAssistantApprover approver,
        TimeAdminManager timeAdmin)
    {
        _cfg = cfg;
        _log = log;
        _approver = approver;
        _timeAdmin = timeAdmin;
    }

    /// <summary>Status einer laufenden Admin-Zeit.</summary>
    public TimeAdminResponse TimeStatus() => new()
    {
        State = RequestState.Approved,
        Active = _timeAdmin.IsActive,
        RemainingSeconds = _timeAdmin.RemainingSeconds,
        Message = _timeAdmin.IsActive
            ? $"Admin-Zeit aktiv, noch {_timeAdmin.RemainingSeconds / 60} Min."
            : "Keine Admin-Zeit aktiv.",
    };

    /// <summary>
    /// Fordert eine zeitlich befristete Admin-Sitzung an. Immer mit Freigabe über
    /// Home Assistant (nie automatisch), wie vom Besitzer vorgegeben.
    /// </summary>
    public async Task<TimeAdminResponse> GrantTimeAsync(string requestingUser, string? reason,
        CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");

        if (_timeAdmin.IsActive)
            return new TimeAdminResponse
            {
                State = RequestState.Denied, Decision = "AlreadyActive",
                Active = true, RemainingSeconds = _timeAdmin.RemainingSeconds,
                Message = $"Es läuft schon eine Admin-Zeit (noch {_timeAdmin.RemainingSeconds / 60} Min).",
            };

        if (!_cfg.HomeAssistant.Enabled)
            return new TimeAdminResponse
            {
                State = RequestState.Denied, Decision = "Policy",
                Message = "Admin-Zeit braucht eine Freigabe, aber Home Assistant ist nicht eingerichtet.",
            };

        _log.Write("timeadmin.requested", new { requestId, requestingUser, minutes = TimeAdminMinutes, reason });

        var ask = new ApprovalAsk
        {
            RequestId = requestId,
            User = requestingUser,
            AppName = $"VOLLE ADMINRECHTE für {TimeAdminMinutes} Minuten",
            Path = "(Zeit-Admin)",
            Reason = reason,
            Secret = _cfg.HomeAssistant.CallbackSecret,
            CallbackUrl = CallbackUrl(),
        };
        var result = await _approver.AskAsync(ask, _cfg.ApprovalTimeoutSeconds, ct);

        if (!result.Approved)
        {
            var decision = result.DecidedBy == "timeout" ? "Timeout" : "HomeAssistant";
            _log.Write("timeadmin.denied", new { requestId, decision });
            return new TimeAdminResponse
            {
                State = RequestState.Denied, Decision = decision,
                Message = decision == "Timeout"
                    ? "Keine Antwort rechtzeitig erhalten."
                    : "Die Anfrage wurde abgelehnt.",
            };
        }

        var (ok, msg) = _timeAdmin.Grant(TimeAdminMinutes, requestId, reason);
        return new TimeAdminResponse
        {
            State = ok ? RequestState.Approved : RequestState.Error,
            Decision = "HomeAssistant",
            Active = ok, RemainingSeconds = ok ? _timeAdmin.RemainingSeconds : 0,
            Message = msg,
        };
    }

    public async Task<ElevationResponse> HandleAsync(ElevationRequest req, string requestingUser,
        CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var app = ResolveApp(req);

        // Unbekanntes Programm: Wenn ein gültiger Dateipfad zu einer .exe vorliegt,
        // bauen wir eine Ad-hoc-Anfrage. Am Handy gibt es dann zusätzlich "Immer erlauben".
        var isAdhoc = false;
        if (app is null)
        {
            var candidate = (req.CustomPath ?? "").Trim('"');
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)
                || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                _log.Write("request.unknown_app", new { requestId, requestingUser, req.AppName, req.CustomPath });
                return new ElevationResponse
                {
                    State = RequestState.Denied,
                    Decision = "Policy",
                    Message = "Programm ist nicht freigegeben. Bitte eine .exe auswählen oder vom Elternteil eintragen lassen.",
                };
            }
            if (!_cfg.HomeAssistant.Enabled)
            {
                return new ElevationResponse
                {
                    State = RequestState.Denied, Decision = "Policy",
                    Message = "Neue Programme brauchen eine Freigabe, aber Home Assistant ist nicht eingerichtet.",
                };
            }
            isAdhoc = true;
            app = new AllowedApp
            {
                Name = Path.GetFileNameWithoutExtension(candidate),
                Path = candidate,
                AutoApprove = false,
                // Herausgeber auslesen, falls signiert – für die spätere Absicherung.
                ExpectedPublisher = TryGetPublisher(candidate),
            };
        }

        _log.Write("request.received", new
        {
            requestId, requestingUser, app.Name, app.Path, app.AutoApprove, isAdhoc, reason = req.Reason,
        });

        // Vor jeder Freigabe die Datei prüfen (Herausgeber/Hash/Pfad).
        var verify = FileVerifier.Verify(app);
        if (!verify.Ok)
        {
            _log.Write("request.verify_failed", new { requestId, app.Name, detail = verify.Detail });
            return new ElevationResponse
            {
                State = RequestState.Denied,
                Decision = "IntegrityCheck",
                Message = $"Sicherheitsprüfung fehlgeschlagen: {verify.Detail}",
            };
        }
        _log.Write("request.verified", new { requestId, app.Name, detail = verify.Detail });

        bool approved;
        bool persist = false;
        string decision;

        if (app.AutoApprove)
        {
            approved = true;
            decision = "Auto";
        }
        else if (!_cfg.HomeAssistant.Enabled)
        {
            approved = false;
            decision = "Policy";
        }
        else
        {
            var ask = new ApprovalAsk
            {
                RequestId = requestId,
                User = requestingUser,
                AppName = app.Name,
                Path = app.Path,
                Reason = req.Reason ?? app.Reason,
                Secret = _cfg.HomeAssistant.CallbackSecret,
                CallbackUrl = CallbackUrl(),
                AllowPersist = isAdhoc,   // "Immer erlauben" nur für neue Programme anbieten
            };
            var result = await _approver.AskAsync(ask, _cfg.ApprovalTimeoutSeconds, ct);
            _log.Write("request.approval_returned", new { requestId, result.Approved, result.Persist, result.DecidedBy });
            approved = result.Approved;
            persist = result.Approved && result.Persist && isAdhoc;
            decision = result.DecidedBy == "timeout" ? "Timeout" : "HomeAssistant";
        }

        // "Immer erlauben": Programm dauerhaft eintragen (dieser Pfad, mit Hash abgesichert).
        if (persist)
        {
            try
            {
                var permanent = new AllowedApp
                {
                    Name = app.Name,
                    Path = app.Path,
                    AutoApprove = true,               // künftig grüne Kachel, sofort
                    ExpectedPublisher = app.ExpectedPublisher,
                    ExpectedSha256 = string.IsNullOrEmpty(app.ExpectedPublisher)
                        ? FileVerifier.Sha256(app.Path)   // ohne Signatur: per Hash absichern
                        : null,
                    Reason = req.Reason ?? "Von Papa dauerhaft erlaubt",
                };
                _cfg.AddAllowedApp(permanent);
                _log.Write("request.persisted", new { requestId, permanent.Name, permanent.Path });
            }
            catch (Exception ex)
            {
                _log.Write("request.persist_failed", new { requestId, app.Name, error = ex.Message });
            }
        }

        if (!approved)
        {
            _log.Write("request.denied", new { requestId, app.Name, decision });
            return new ElevationResponse
            {
                State = RequestState.Denied,
                Decision = decision,
                Message = decision == "Timeout"
                    ? "Keine Antwort rechtzeitig erhalten. Bitte erneut anfragen."
                    : "Die Anfrage wurde abgelehnt.",
            };
        }

        _log.Write("launch.begin", new { requestId, app.Name, app.Path });
        var launch = _elevator.LaunchElevated(app.Path, app.Arguments, null,
            step => _log.Write("launch.step", new { requestId, app.Name, step }));

        if (!launch.Ok)
        {
            _log.Write("launch.failed", new { requestId, app.Name, detail = launch.Detail });
            return new ElevationResponse
            {
                State = RequestState.Error, Decision = decision,
                Message = $"Start fehlgeschlagen: {launch.Detail}",
            };
        }

        _log.Write("launch.success", new { requestId, app.Name, launch.ProcessId, decision });
        return new ElevationResponse
        {
            State = RequestState.Approved, Decision = decision,
            Message = $"{app.Name} wurde gestartet.",
        };
    }

    private string? _cachedCallbackHost;

    /// <summary>
    /// Adresse, unter der HA den Rückkanal erreicht. Bevorzugt die lokale LAN-IP
    /// (Hostnamen-Auflösung von HA aus ist oft nicht gegeben), Fallback Hostname.
    /// </summary>
    private string CallbackUrl()
    {
        _cachedCallbackHost ??= ResolveLocalIp() ?? Environment.MachineName;
        return $"http://{_cachedCallbackHost}:{_cfg.HomeAssistant.CallbackPort}/decision/";
    }

    private static string? ResolveLocalIp()
    {
        try
        {
            // Die IP ermitteln, über die ausgehende Verbindungen laufen (ohne zu senden).
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is System.Net.IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }
        return null;
    }

    /// <summary>Herausgeber aus der Signatur lesen; null, wenn unsigniert/ungültig.</summary>
    private static string? TryGetPublisher(string path)
    {
        var (ok, subject) = FileVerifier.AuthenticodePublisher(path);
        if (!ok || string.IsNullOrWhiteSpace(subject)) return null;
        // Aus dem Zertifikat-Subject den CN herausziehen (z. B. CN="Valve Corp.").
        foreach (var part in subject.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return p[3..].Trim('"', ' ');
        }
        return null;
    }

    /// <summary>Prüft, ob das Kind-Konto dauerhaft Admin ist (Tool waere dann wirkungslos).</summary>
    public AdminCheckResponse AdminCheck()
    {
        var user = Elevator.GetActiveSessionUser();
        if (user is null)
            return new AdminCheckResponse { State = RequestState.Error, Message = "Keine aktive Sitzung." };

        // Während einer aktiven Zeit-Admin-Session ist er legitim Admin – nicht warnen.
        if (_timeAdmin.IsActive)
            return new AdminCheckResponse { State = RequestState.Approved, IsPermanentAdmin = false,
                Account = user.Value.Full, Message = "Admin-Zeit aktiv." };

        var perm = Elevator.IsPermanentAdmin(user.Value.Sid);
        _log.Write("admincheck", new { user = user.Value.Full, permanentAdmin = perm });
        return new AdminCheckResponse
        {
            State = RequestState.Approved,
            IsPermanentAdmin = perm,
            Account = user.Value.Full,
            Message = perm
                ? "Dieses Konto ist dauerhaft Administrator. RunAsAdmin kann nichts kontrollieren, solange das so ist."
                : "Konto ist Standardbenutzer – alles gut.",
        };
    }

    /// <summary>Stuft das Kind-Konto auf Standardbenutzer herunter (nur mit korrekter PIN).</summary>
    public AdminCheckResponse Demote(string? pin)
    {
        if (string.IsNullOrEmpty(_cfg.ParentPinHash))
            return new AdminCheckResponse { State = RequestState.Denied, Message = "Erst eine Eltern-PIN festlegen." };
        if (string.IsNullOrEmpty(pin) || !Secret.VerifyPin(pin, _cfg.ParentPinHash))
            return new AdminCheckResponse { State = RequestState.Denied, Message = "Falsche PIN." };

        var user = Elevator.GetActiveSessionUser();
        if (user is null)
            return new AdminCheckResponse { State = RequestState.Error, Message = "Keine aktive Sitzung." };
        try
        {
            Elevator.DemoteFromAdministrators(user.Value.Sid);
            _log.Write("admin.demoted", new { user = user.Value.Full });
            return new AdminCheckResponse { State = RequestState.Approved, IsPermanentAdmin = false,
                Account = user.Value.Full,
                Message = "Konto auf Standardbenutzer heruntergestuft. Bitte neu anmelden, damit es wirkt." };
        }
        catch (Exception ex)
        {
            return new AdminCheckResponse { State = RequestState.Error, Message = $"Fehlgeschlagen: {ex.Message}" };
        }
    }

    /// <summary>Verlauf der letzten relevanten Ereignisse aus dem Audit-Log.</summary>
    public HistoryResponse History(int max = 30)
    {
        var resp = new HistoryResponse();
        try
        {
            var file = Path.Combine(_cfg.LogDirectory, $"audit-{DateTime.Now:yyyy-MM-dd}.jsonl");
            if (!File.Exists(file)) return resp;
            var lines = File.ReadLines(file).ToArray();
            foreach (var line in lines.Reverse())
            {
                if (resp.Items.Count >= max) break;
                var item = ParseHistory(line);
                if (item is not null) resp.Items.Add(item);
            }
        }
        catch { }
        return resp;
    }

    private static HistoryItem? ParseHistory(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = root.GetProperty("event").GetString() ?? "";
            var ts = root.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
            var time = DateTimeOffset.TryParse(ts, out var dto) ? dto.ToString("HH:mm") : "";
            var data = root.TryGetProperty("data", out var d) ? d : default;
            string? name = data.ValueKind == System.Text.Json.JsonValueKind.Object
                && data.TryGetProperty("Name", out var n) ? n.GetString() : null;

            return ev switch
            {
                "timeadmin.granted" => new HistoryItem { Time = time, Kind = "grant", Text = "Adminrechte freigegeben (15 Min)" },
                "timeadmin.revoked" => new HistoryItem { Time = time, Kind = "info", Text = "Adminrechte abgelaufen" },
                "timeadmin.denied" => new HistoryItem { Time = time, Kind = "deny", Text = "Adminrechte abgelehnt" },
                "request.denied" => new HistoryItem { Time = time, Kind = "deny", Text = $"Abgelehnt: {name}" },
                "launch.success" => new HistoryItem { Time = time, Kind = "grant", Text = $"Gestartet: {name}" },
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>PIN-Status (ist schon eine Eltern-PIN gesetzt?).</summary>
    public SettingsResponse PinStatus() => new()
    {
        State = RequestState.Approved,
        PinIsSet = !string.IsNullOrEmpty(_cfg.ParentPinHash),
    };

    /// <summary>Liefert die aktuellen Einstellungen (ohne Secret-Klartext).</summary>
    public SettingsResponse GetSettings()
    {
        var ha = _cfg.HomeAssistant;
        return new SettingsResponse
        {
            State = RequestState.Approved,
            PinIsSet = !string.IsNullOrEmpty(_cfg.ParentPinHash),
            Settings = new SettingsPayload
            {
                BaseUrl = ha.BaseUrl,
                RequestWebhookId = ha.RequestWebhookId,
                EventWebhookId = ha.EventWebhookId,
                CallbackPort = ha.CallbackPort,
                Enabled = ha.Enabled,
                ReportEvents = ha.ReportEvents,
                SecretIsSet = !string.IsNullOrEmpty(ha.CallbackSecretEnc),
            },
        };
    }

    /// <summary>
    /// Speichert Einstellungen. Nur erlaubt, wenn die PIN stimmt – oder wenn noch
    /// gar keine PIN gesetzt ist (Ersteinrichtung), dann muss NewPin gesetzt werden.
    /// </summary>
    public SettingsResponse SaveSettings(SettingsPayload p, string requestingUser)
    {
        var pinSet = !string.IsNullOrEmpty(_cfg.ParentPinHash);

        if (pinSet)
        {
            if (string.IsNullOrEmpty(p.Pin) || !Secret.VerifyPin(p.Pin, _cfg.ParentPinHash))
            {
                _log.Write("settings.pin_wrong", new { requestingUser });
                return new SettingsResponse { State = RequestState.Denied, PinIsSet = true,
                    Message = "Falsche PIN." };
            }
        }
        else
        {
            // Ersteinrichtung: es MUSS eine neue PIN gesetzt werden.
            if (string.IsNullOrWhiteSpace(p.NewPin) || p.NewPin.Length < 4)
                return new SettingsResponse { State = RequestState.Denied, PinIsSet = false,
                    Message = "Bitte zuerst eine Eltern-PIN (mind. 4 Zeichen) festlegen." };
        }

        var ha = _cfg.HomeAssistant;
        ha.BaseUrl = (p.BaseUrl ?? "").TrimEnd('/');
        ha.RequestWebhookId = string.IsNullOrWhiteSpace(p.RequestWebhookId) ? ha.RequestWebhookId : p.RequestWebhookId;
        ha.EventWebhookId = string.IsNullOrWhiteSpace(p.EventWebhookId) ? ha.EventWebhookId : p.EventWebhookId;
        ha.CallbackPort = p.CallbackPort > 0 ? p.CallbackPort : ha.CallbackPort;
        ha.Enabled = p.Enabled;
        ha.ReportEvents = p.ReportEvents;

        if (!string.IsNullOrEmpty(p.NewSecret)) ha.SetSecret(p.NewSecret);
        if (!string.IsNullOrEmpty(p.NewPin)) _cfg.ParentPinHash = Secret.HashPin(p.NewPin);

        try
        {
            if (!string.IsNullOrEmpty(_cfg.SourcePath)) _cfg.Save(_cfg.SourcePath);
            _log.Write("settings.saved", new { requestingUser, ha.BaseUrl, ha.Enabled, secretSet = !string.IsNullOrEmpty(ha.CallbackSecretEnc) });
            return new SettingsResponse { State = RequestState.Approved, PinIsSet = true,
                Message = "Einstellungen gespeichert." };
        }
        catch (Exception ex)
        {
            _log.Write("settings.save_failed", new { error = ex.Message });
            return new SettingsResponse { State = RequestState.Error, PinIsSet = pinSet,
                Message = $"Speichern fehlgeschlagen: {ex.Message}" };
        }
    }

    /// <summary>Liefert die anzeigbaren Programme für die GUI (ohne interne Felder).</summary>
    public AppListResponse ListApps()
    {
        return new AppListResponse
        {
            Apps = _cfg.AllowedApps.Select(a => new AppInfo
            {
                Name = a.Name,
                AutoApprove = a.AutoApprove,
                Reason = a.Reason,
                Available = !string.IsNullOrWhiteSpace(a.Path) && File.Exists(a.Path),
            }).ToList(),
        };
    }

    /// <summary>Findet die passende Regel. Benannte Apps nur über die Freigabeliste.</summary>
    private AllowedApp? ResolveApp(ElevationRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.AppName))
        {
            return _cfg.AllowedApps.FirstOrDefault(a =>
                string.Equals(a.Name, req.AppName, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(req.CustomPath))
        {
            // Ein freier Pfad wird nur akzeptiert, wenn er exakt einer Regel entspricht.
            return _cfg.AllowedApps.FirstOrDefault(a =>
                string.Equals(a.Path, req.CustomPath, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }
}
