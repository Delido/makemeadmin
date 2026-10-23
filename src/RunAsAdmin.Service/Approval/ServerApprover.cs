using System.Net.Http.Json;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Approval;

/// <summary>
/// Freigabe über das RunAsAdmin-Server-Add-on (FastAPI). Der Dienst schickt die
/// Anfrage an <c>POST /api/request</c> und fragt danach <c>POST /api/status</c>
/// so lange ab, bis eine Entscheidung vorliegt oder das Timeout erreicht ist.
/// Anders als beim Webhook-Modus ist am PC KEIN offener Port nötig.
/// Ein Not-Aus vom Dashboard wird über <c>POST /api/cancel-status</c> gepollt.
/// </summary>
public sealed class ServerApprover : IApprover
{
    private readonly ServerConfig _cfg;
    private readonly AuditLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _cts;

    // Laufende Dienst-Version (fuer die Geraete-Uebersicht im Dashboard).
    private static readonly string _version =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public ServerApprover(ServerConfig cfg, AuditLog log)
    {
        _cfg = cfg;
        _log = log;
    }

    public Action? OnCancel { get; set; }

    /// <summary>Startet die Not-Aus-Überwachung (pollt das Add-on im Hintergrund).</summary>
    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_cfg.BaseUrl)) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => CancelWatchLoop(_cts.Token));
    }

    private string Url(string path) => $"{_cfg.BaseUrl.TrimEnd('/')}{path}";

    public async Task<ApprovalDecision> AskAsync(ApprovalAsk ask, int timeoutSeconds, CancellationToken ct)
    {
        // 1) Anfrage anlegen. PascalCase-Felder, passend zur Add-on-API.
        var body = new
        {
            Token = _cfg.Token,
            RequestId = ask.RequestId,
            Computer = ask.Computer,
            User = ask.User,
            AppName = ask.AppName,
            Reason = ask.Reason ?? "",
        };
        try
        {
            using var content = new StringContent(AppConfig.SerializeCompact(body),
                Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(Url("/api/request"), content, ct);
            _log.Write("approval.asked", new { ask.RequestId, ask.AppName, status = (int)resp.StatusCode, via = "server" });
            if (!resp.IsSuccessStatusCode)
                return new ApprovalDecision { RequestId = ask.RequestId, Approved = false, DecidedBy = "error" };
        }
        catch (Exception ex)
        {
            _log.Write("approval.ask_failed", new { ask.RequestId, error = ex.Message, via = "server" });
            return new ApprovalDecision { RequestId = ask.RequestId, Approved = false, DecidedBy = "error" };
        }

        // 2) Entscheidung pollen, bis allow/deny oder Timeout.
        var deadline = DateTimeOffset.Now.AddSeconds(timeoutSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _cfg.PollSeconds));
        while (DateTimeOffset.Now < deadline)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            string status;
            try
            {
                status = await QueryStatusAsync(ask.RequestId, ct);
            }
            catch (Exception ex)
            {
                _log.Write("approval.poll_failed", new { ask.RequestId, error = ex.Message });
                continue;   // vorübergehender Fehler – weiter versuchen bis zum Timeout
            }

            if (status is "allow" or "allow_always")
            {
                var persist = status == "allow_always";
                _log.Write("decision.received", new { ok = true, ask.RequestId, Approved = true, persist, via = "server" });
                return new ApprovalDecision { RequestId = ask.RequestId, Approved = true, Persist = persist, DecidedBy = "server" };
            }
            if (status is "deny")
            {
                _log.Write("decision.received", new { ok = true, ask.RequestId, Approved = false, via = "server" });
                return new ApprovalDecision { RequestId = ask.RequestId, Approved = false, DecidedBy = "server" };
            }
            // "pending" / "unknown" -> weiter warten
        }

        _log.Write("approval.timeout", new { ask.RequestId, timeoutSeconds, via = "server" });
        return new ApprovalDecision { RequestId = ask.RequestId, Approved = false, DecidedBy = "timeout" };
    }

    private async Task<string> QueryStatusAsync(string requestId, CancellationToken ct)
    {
        var body = new { Token = _cfg.Token, RequestId = requestId };
        using var content = new StringContent(AppConfig.SerializeCompact(body),
            Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(Url("/api/status"), content, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var parsed = AppConfig.Deserialize<StatusResponse>(json);
        return parsed?.Status ?? "unknown";
    }

    /// <summary>
    /// Fragt das Add-on in Abständen, ob für diesen PC ein Not-Aus ansteht. Kommt
    /// einer, wird <see cref="OnCancel"/> ausgelöst (beendet die laufende Admin-Zeit).
    /// </summary>
    private async Task CancelWatchLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(3, _cfg.PollSeconds * 2));
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                // Version mitsenden -> Geraete-Uebersicht im Dashboard (Version/online).
                var body = new { Token = _cfg.Token, Computer = Environment.MachineName, Version = _version };
                using var content = new StringContent(AppConfig.SerializeCompact(body),
                    Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(Url("/api/cancel-status"), content, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    var parsed = AppConfig.Deserialize<CancelResponse>(json);
                    if (parsed?.Cancel == true)
                    {
                        _log.Write("timeadmin.cancel_requested", new { by = "server" });
                        try { OnCancel?.Invoke(); } catch { }
                    }
                }

                // Zusätzlich: offene App-Entfernungen vom Dashboard abholen.
                using var rc = new StringContent(AppConfig.SerializeCompact(body),
                    Encoding.UTF8, "application/json");
                var rresp = await _http.PostAsync(Url("/api/remove-status"), rc, ct);
                if (rresp.IsSuccessStatusCode)
                {
                    var rjson = await rresp.Content.ReadAsStringAsync(ct);
                    var rparsed = AppConfig.Deserialize<RemoveResponse>(rjson);
                    if (rparsed?.Remove is { Count: > 0 } names)
                    {
                        _log.Write("app.remove_requested", new { by = "server", names });
                        try { OnRemoveGames?.Invoke(names); } catch { }
                    }
                }
            }
            catch { /* Add-on evtl. offline – egal, nächster Versuch */ }
        }
    }

    /// <summary>Wird ausgelöst, wenn das Dashboard App-Freigaben entfernen will (Namen).</summary>
    public Action<List<string>>? OnRemoveGames { get; set; }

    private sealed class StatusResponse { public string? Status { get; set; } }
    private sealed class CancelResponse { public bool Cancel { get; set; } }
    private sealed class RemoveResponse { public List<string>? Remove { get; set; } }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _http.Dispose();
        await Task.CompletedTask;
    }
}
