using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Approval;

/// <summary>
/// Schickt Freigabeanfragen per Webhook an Home Assistant und wartet auf die
/// Entscheidung, die Home Assistant an einen lokalen HTTP-Listener zurückschickt.
/// Der Rückkanal ist mit einem gemeinsamen Secret pro Anfrage abgesichert.
/// </summary>
public sealed class HomeAssistantApprover : IAsyncDisposable
{
    private readonly HomeAssistantConfig _cfg;
    private readonly AuditLog _log;
    private readonly HttpClient _http = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new();
    private CancellationTokenSource? _cts;

    public HomeAssistantApprover(HomeAssistantConfig cfg, AuditLog log)
    {
        _cfg = cfg;
        _log = log;
    }

    public void Start()
    {
        if (!_cfg.Enabled) return;
        _listener.Prefixes.Add($"http://+:{_cfg.CallbackPort}/decision/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync(ct);
                    var decision = AppConfig.Deserialize<ApprovalDecision>(body);

                    if (decision is null)
                    {
                        _log.Write("decision.received", new { ok = false, reason = "kein/ungültiges JSON" });
                        ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    }
                    else if (!string.Equals(decision.Secret, _cfg.CallbackSecret, StringComparison.Ordinal))
                    {
                        _log.Write("decision.received", new { ok = false, reason = "Secret passt nicht", decision.RequestId });
                        ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    }
                    else if (_pending.TryRemove(decision.RequestId, out var tcs))
                    {
                        _log.Write("decision.received", new { ok = true, decision.RequestId, decision.Approved, decision.Persist });
                        tcs.TrySetResult(decision);
                        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                    }
                    else
                    {
                        _log.Write("decision.received", new { ok = false, reason = "Anfrage unbekannt/abgelaufen", decision.RequestId });
                        ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    }
                }
                catch (Exception ex) { _log.Write("decision.error", new { error = ex.Message }); ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest; }
                finally { ctx.Response.Close(); }
            }, ct);
        }
    }

    /// <summary>Fragt Home Assistant und wartet bis zur Entscheidung oder zum Timeout.</summary>
    public async Task<ApprovalDecision> AskAsync(ApprovalAsk ask, int timeoutSeconds, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[ask.RequestId] = tcs;

        var url = $"{_cfg.BaseUrl.TrimEnd('/')}/api/webhook/{_cfg.RequestWebhookId}";
        try
        {
            // WICHTIG: PascalCase beibehalten (HA-Automation liest req.User, req.AppName …).
            // Ohne eigene Optionen würde PostAsJsonAsync camelCase senden -> leere Felder.
            var json = AppConfig.SerializeCompact(ask);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content, ct);
            _log.Write("approval.asked", new { ask.RequestId, ask.AppName, status = (int)resp.StatusCode });
        }
        catch (Exception ex)
        {
            _pending.TryRemove(ask.RequestId, out _);
            _log.Write("approval.ask_failed", new { ask.RequestId, error = ex.Message });
            return new ApprovalDecision { RequestId = ask.RequestId, Approved = false };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(ask.RequestId, out _);
            _log.Write("approval.timeout", new { ask.RequestId, timeoutSeconds });
            return new ApprovalDecision { RequestId = ask.RequestId, Approved = false, DecidedBy = "timeout" };
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listener.IsListening) _listener.Stop();
        _http.Dispose();
        await Task.CompletedTask;
    }
}
