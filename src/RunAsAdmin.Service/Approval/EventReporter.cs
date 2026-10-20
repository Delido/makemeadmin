using System.Net.Http.Json;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Approval;

/// <summary>
/// Meldet ausgewählte Protokoll-Ereignisse an Home Assistant (Webhook), damit
/// sie dort im Logbuch erscheinen und für Statistik/Dashboard nutzbar sind.
/// Feuert nur für relevante Ereignisse, nicht für jede interne Kleinigkeit.
/// </summary>
public sealed class EventReporter : IDisposable
{
    // An HA nur Zusammenfassungen. Die vollständige Prozessliste während einer
    // Admin-Zeit (timeadmin.process) bleibt bewusst lokal/fälschungssicher.
    private static readonly HashSet<string> Relevant = new(StringComparer.OrdinalIgnoreCase)
    {
        "launch.success", "request.denied", "request.persisted",
        "timeadmin.granted", "timeadmin.revoked",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _url;
    private readonly bool _enabled;

    public EventReporter(HomeAssistantConfig cfg)
    {
        _enabled = cfg.Enabled && cfg.ReportEvents && !string.IsNullOrWhiteSpace(cfg.BaseUrl);
        _url = $"{cfg.BaseUrl.TrimEnd('/')}/api/webhook/{cfg.EventWebhookId}";
    }

    public void Report(string eventType, object data)
    {
        if (!_enabled || !Relevant.Contains(eventType)) return;
        // Feuern und vergessen – Protokollierung darf den Dienst nie blockieren.
        _ = Task.Run(async () =>
        {
            try
            {
                await _http.PostAsJsonAsync(_url, new
                {
                    computer = Environment.MachineName,
                    @event = eventType,
                    ts = DateTimeOffset.Now.ToString("o"),
                    data,
                });
            }
            catch { /* HA evtl. offline – egal, lokal ist alles protokolliert */ }
        });
    }

    public void Dispose() => _http.Dispose();
}
