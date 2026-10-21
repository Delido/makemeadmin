using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using RunAsAdmin.Shared;

namespace RunAsAdmin.Service;

/// <summary>
/// Named-Pipe-Server, über den das Kinder-Tool Anfragen stellt. Der Dienst
/// ermittelt den anfragenden Benutzer sicher über die Pipe-Identität, nicht
/// aus dem Nachrichteninhalt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeServer
{
    private readonly AppConfig _cfg;
    private readonly AuditLog _log;
    private readonly RequestHandler _handler;

    public PipeServer(AppConfig cfg, AuditLog log, RequestHandler handler)
    {
        _cfg = cfg;
        _log = log;
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(ct);
                // Eigentum geht an den Handler über; dort wird die Pipe disposed.
                // Deshalb hier KEIN using – sonst wird die Verbindung sofort geschlossen.
                _ = HandleConnectionAsync(server, ct);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                server?.Dispose();
                _log.Write("pipe.error", new { error = ex.Message });
                await Task.Delay(500, ct);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // Jeder authentifizierte Benutzer darf lesen/schreiben; nur SYSTEM/Admin
        // dürfen die Pipe erstellen (das tut der Dienst).
        var security = new PipeSecurity();
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(users,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create($"\\\\.\\pipe\\{_cfg.PipeName}".Replace("\\\\.\\pipe\\", ""),
            PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            // Zuerst lesen: der Identitätswechsel über die Pipe ist erst nach dem
            // ersten Lesevorgang möglich. Wir lesen eine newline-terminierte Zeile
            // byteweise, ohne StreamReader (der puffert und schließt den Stream).
            var json = await ReadLineAsync(server, ct);

            // Anfragenden Benutzer sicher aus der Pipe-Identität ermitteln.
            string user = "unbekannt";
            try
            {
                server.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    user = id.Name;
                });
            }
            catch (Exception ex) { _log.Write("pipe.impersonate_failed", new { error = ex.Message }); }

            var msg = json is null ? null : AppConfig.Deserialize<PipeMessage>(json);

            object responseObject;
            if (msg is null)
            {
                responseObject = new ElevationResponse { State = RequestState.Error, Message = "Ungültige Anfrage." };
            }
            else if (msg.Verb == PipeVerb.List)
            {
                responseObject = _handler.ListApps();
            }
            else if (msg.Verb == PipeVerb.TimeStatus)
            {
                responseObject = _handler.TimeStatus();
            }
            else if (msg.Verb == PipeVerb.GrantTime)
            {
                responseObject = await _handler.GrantTimeAsync(user, msg.Reason, ct);
            }
            else if (msg.Verb == PipeVerb.ApplyUpdate)
            {
                // Sicherheit: der Dienst lädt + prüft das Update SELBST von GitHub.
                // Ein vom Client mitgeschicktes ZIP wird bewusst ignoriert.
                _log.Write("update.requested", new { by = user });
                responseObject = UpdateApplier.CheckAndApply(_log);
            }
            else if (msg.Verb == PipeVerb.PinStatus)
            {
                responseObject = _handler.PinStatus();
            }
            else if (msg.Verb == PipeVerb.History)
            {
                responseObject = _handler.History();
            }
            else if (msg.Verb == PipeVerb.GetSettings)
            {
                responseObject = _handler.GetSettings();
            }
            else if (msg.Verb == PipeVerb.SaveSettings)
            {
                responseObject = _handler.SaveSettings(msg.Settings ?? new SettingsPayload(), user);
            }
            else if (msg.Request is null)
            {
                responseObject = new ElevationResponse { State = RequestState.Error, Message = "Keine Anfrage enthalten." };
            }
            else
            {
                responseObject = await _handler.HandleAsync(msg.Request, user, ct);
            }

            var payload = new UTF8Encoding(false).GetBytes(AppConfig.SerializeCompact(responseObject) + "\n");
            await server.WriteAsync(payload, ct);
            await server.FlushAsync(ct);
            server.WaitForPipeDrain();
        }
        catch (Exception ex)
        {
            _log.Write("pipe.handle_error", new { error = ex.Message, type = ex.GetType().Name,
                stack = ex.StackTrace?.Split('\n').Take(3).ToArray() });
        }
        finally
        {
            try { if (server.IsConnected) server.Disconnect(); } catch { }
            server.Dispose();
        }
    }

    /// <summary>Liest byteweise bis zum Zeilenumbruch (ohne den Stream zu schließen).</summary>
    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) break;               // Gegenseite geschlossen
            if (one[0] == (byte)'\n') break; // Zeilenende
            ms.WriteByte(one[0]);
        }
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
    }
}
