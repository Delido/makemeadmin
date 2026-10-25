using System.Net.Http.Json;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Approval;

/// <summary>
/// Meldet ausgewählte Protokoll-Ereignisse nach außen (Logbuch/Dashboard), je nach
/// Modus an den Home-Assistant-Webhook oder an das RunAsAdmin-Server-Add-on.
/// Feuert nur für relevante Ereignisse, nicht für jede interne Kleinigkeit.
/// </summary>
public sealed class EventReporter : IDisposable
{
    // An HA/Server nur Zusammenfassungen. Die vollständige Prozessliste während einer
    // Admin-Zeit (timeadmin.process) bleibt bewusst lokal/fälschungssicher.
    private static readonly HashSet<string> Relevant = new(StringComparer.OrdinalIgnoreCase)
    {
        "launch.success", "request.denied", "request.persisted",
        "timeadmin.granted", "timeadmin.revoked",
        "timeadmin.local_pin_wrong",   // Fehlversuch beim lokalen PIN-Fallback melden
        "game.launched", "games.registered",   // Spiele: Start + Liste der freigegebenen
        "admin.demoted", "admin.permanent_warning",
        "admin.suspicious_alert",   // Missbrauch waehrend kurz-Admin -> Push an Papa
        // Diagnose: ob der Rückkanal-Listener startet und Entscheidungen ankommen.
        "service.ha_listener_failed", "decision.received",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ApprovalMode _mode;
    private readonly bool _enabled;
    private readonly string _url;
    private readonly string _token;

    public EventReporter(AppConfig cfg)
    {
        _mode = cfg.Mode;
        if (_mode == ApprovalMode.Server)
        {
            var s = cfg.Server;
            _enabled = s.ReportEvents && !string.IsNullOrWhiteSpace(s.BaseUrl);
            _url = $"{s.BaseUrl.TrimEnd('/')}/api/event";
            _token = s.Token;
        }
        else
        {
            var ha = cfg.HomeAssistant;
            _enabled = ha.Enabled && ha.ReportEvents && !string.IsNullOrWhiteSpace(ha.BaseUrl);
            _url = $"{ha.BaseUrl.TrimEnd('/')}/api/webhook/{ha.EventWebhookId}";
            _token = "";
        }
    }

    public void Report(string eventType, object data)
    {
        if (!_enabled || !Relevant.Contains(eventType)) return;
        // Feuern und vergessen – Protokollierung darf den Dienst nie blockieren.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_mode == ApprovalMode.Server)
                    await PostServerAsync(eventType, data);
                else
                    await _http.PostAsJsonAsync(_url, new
                    {
                        computer = Environment.MachineName,
                        @event = eventType,
                        ts = DateTimeOffset.Now.ToString("o"),
                        data,
                    });
            }
            catch { /* HA/Server evtl. offline – egal, lokal ist alles protokolliert */ }
        });
    }

    /// <summary>Meldet ein Ereignis an das Add-on (PascalCase, mit Token).</summary>
    private async Task PostServerAsync(string eventType, object data)
    {
        // Zusatzfelder, die das Add-on für Sitzungen/Statistik nutzt (User, Apps).
        string user = TryGet(data, "user") ?? TryGet(data, "requestingUser") ?? "";
        // Bevorzugt den fertigen Komma-String 'appsText' (timeadmin.revoked); das Feld
        // 'apps' ist ein string[] und wuerde sonst als "System.String[]" landen.
        string apps = TryGet(data, "appsText") ?? TryGet(data, "apps") ?? "";
        var body = new
        {
            Token = _token,
            Computer = Environment.MachineName,
            Type = eventType,
            Detail = AppConfig.SerializeCompact(data),
            User = user,
            Apps = apps,
        };
        using var content = new StringContent(AppConfig.SerializeCompact(body),
            Encoding.UTF8, "application/json");
        await _http.PostAsync(_url, content);
    }

    /// <summary>
    /// Liest ein Feld (case-insensitiv) aus einem anonymen Objekt, falls vorhanden.
    /// Sammlungen (z. B. string[]) werden zu einem Komma-String zusammengefügt, damit
    /// nicht "System.String[]" nach außen geht.
    /// </summary>
    private static string? TryGet(object data, string name)
    {
        var p = data.GetType().GetProperties()
            .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        var val = p?.GetValue(data);
        if (val is null) return null;
        if (val is string s) return s;
        if (val is System.Collections.IEnumerable en)
            return string.Join(", ", en.Cast<object>().Select(o => o?.ToString() ?? ""));
        return val.ToString();
    }

    public void Dispose() => _http.Dispose();
}
