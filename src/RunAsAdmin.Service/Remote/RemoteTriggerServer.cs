using System.Net;
using System.Runtime.Versioning;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Remote;

/// <summary>
/// Schmaler, mit Secret abgesicherter HTTP-Endpunkt, über den der Elternteil
/// (z. B. per Raycast vom eigenen PC) auf diesem Kinder-PC den Start eines
/// freigegebenen Programms auslösen kann. Nutzt denselben RequestHandler wie
/// das lokale Kinder-Tool; die Freigabe (Auto/HA) greift also genauso.
/// Lauscht auf CallbackPort+1.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteTriggerServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RequestHandler _handler;
    private readonly AuditLog _log;
    private readonly string _secret;
    private readonly int _port;
    private CancellationTokenSource? _cts;

    public RemoteTriggerServer(RequestHandler handler, AuditLog log, HomeAssistantConfig ha)
    {
        _handler = handler;
        _log = log;
        _secret = ha.CallbackSecret;
        _port = ha.CallbackPort + 1;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://+:{_port}/trigger/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
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
                    var trigger = AppConfig.Deserialize<RemoteTrigger>(body);

                    if (trigger is null
                        || string.IsNullOrEmpty(_secret)
                        || !string.Equals(trigger.Secret, _secret, StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        _log.Write("remote.rejected", new { reason = "bad secret" });
                    }
                    else
                    {
                        _log.Write("remote.trigger", new { trigger.AppName, by = "remote" });
                        var resp = await _handler.HandleAsync(
                            new ElevationRequest { AppName = trigger.AppName, Reason = trigger.Reason ?? "Ferngestartet" },
                            $"remote:{trigger.Operator ?? "eltern"}", ct);
                        ctx.Response.StatusCode = resp.State == RequestState.Approved
                            ? (int)HttpStatusCode.OK : (int)HttpStatusCode.Accepted;
                        var bytes = Encoding.UTF8.GetBytes(AppConfig.Serialize(resp));
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
                    }
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    _log.Write("remote.error", new { error = ex.Message });
                }
                finally { ctx.Response.Close(); }
            }, ct);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_listener.IsListening) _listener.Stop();
    }
}

/// <summary>Fernauslöser vom Eltern-PC.</summary>
public sealed class RemoteTrigger
{
    public string AppName { get; set; } = "";
    public string? Reason { get; set; }
    public string? Operator { get; set; }
    public string Secret { get; set; } = "";
}
