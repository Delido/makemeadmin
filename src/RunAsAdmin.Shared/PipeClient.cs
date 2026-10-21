using System.IO.Pipes;
using System.Text;

namespace RunAsAdmin.Shared;

/// <summary>
/// Client-seitige Pipe-Kommunikation mit dem Dienst. Von CLI und GUI genutzt.
/// </summary>
public static class PipeClient
{
    public static string PipeName =>
        Environment.GetEnvironmentVariable("RUNASADMIN_PIPE") ?? "RunAsAdmin";

    /// <summary>Fragt die anzeigbaren Programme ab.</summary>
    public static async Task<AppListResponse> ListAppsAsync(CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.List }, ct);
        return (line is null ? null : AppConfig.Deserialize<AppListResponse>(line))
               ?? new AppListResponse();
    }

    /// <summary>Fordert den Start eines Programms an und wartet auf die Entscheidung.</summary>
    public static async Task<ElevationResponse> RunAsync(string appName, string? reason = null,
        CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage
        {
            Verb = PipeVerb.Run,
            Request = new ElevationRequest { AppName = appName, Reason = reason },
        }, ct);
        return (line is null ? null : AppConfig.Deserialize<ElevationResponse>(line))
               ?? new ElevationResponse { State = RequestState.Error, Message = "Keine Antwort erhalten." };
    }

    /// <summary>Holt den Verlauf der letzten Ereignisse.</summary>
    public static async Task<HistoryResponse> HistoryAsync(CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.History }, ct);
        return (line is null ? null : AppConfig.Deserialize<HistoryResponse>(line)) ?? new HistoryResponse();
    }

    /// <summary>Fragt, ob schon eine Eltern-PIN gesetzt ist.</summary>
    public static async Task<SettingsResponse> PinStatusAsync(CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.PinStatus }, ct);
        return (line is null ? null : AppConfig.Deserialize<SettingsResponse>(line))
               ?? new SettingsResponse { State = RequestState.Error, Message = "Keine Antwort." };
    }

    /// <summary>Lädt die aktuellen Einstellungen (ohne Secret-Klartext).</summary>
    public static async Task<SettingsResponse> GetSettingsAsync(CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.GetSettings }, ct);
        return (line is null ? null : AppConfig.Deserialize<SettingsResponse>(line))
               ?? new SettingsResponse { State = RequestState.Error, Message = "Keine Antwort." };
    }

    /// <summary>Speichert Einstellungen (Dienst prüft die PIN).</summary>
    public static async Task<SettingsResponse> SaveSettingsAsync(SettingsPayload p, CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.SaveSettings, Settings = p }, ct);
        return (line is null ? null : AppConfig.Deserialize<SettingsResponse>(line))
               ?? new SettingsResponse { State = RequestState.Error, Message = "Keine Antwort." };
    }

    /// <summary>Bittet den Dienst (SYSTEM), ein heruntergeladenes Update-ZIP einzuspielen.</summary>
    public static async Task<ElevationResponse> ApplyUpdateAsync(string zipPath,
        CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.ApplyUpdate, UpdateZipPath = zipPath }, ct);
        return (line is null ? null : AppConfig.Deserialize<ElevationResponse>(line))
               ?? new ElevationResponse { State = RequestState.Error, Message = "Keine Antwort erhalten." };
    }

    /// <summary>Fragt eine unbekannte .exe über ihren Pfad zur Freigabe an.</summary>
    public static async Task<ElevationResponse> RequestPathAsync(string path, string? reason = null,
        CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage
        {
            Verb = PipeVerb.Run,
            Request = new ElevationRequest { CustomPath = path, Reason = reason },
        }, ct);
        return (line is null ? null : AppConfig.Deserialize<ElevationResponse>(line))
               ?? new ElevationResponse { State = RequestState.Error, Message = "Keine Antwort erhalten." };
    }

    /// <summary>Fordert eine zeitlich befristete Admin-Sitzung an (mit Freigabe).</summary>
    public static async Task<TimeAdminResponse> GrantTimeAsync(string? reason = null,
        CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.GrantTime, Reason = reason }, ct);
        return (line is null ? null : AppConfig.Deserialize<TimeAdminResponse>(line))
               ?? new TimeAdminResponse { State = RequestState.Error, Message = "Keine Antwort erhalten." };
    }

    /// <summary>Fragt den Status einer laufenden Admin-Zeit ab.</summary>
    public static async Task<TimeAdminResponse> TimeStatusAsync(CancellationToken ct = default)
    {
        var line = await SendAsync(new PipeMessage { Verb = PipeVerb.TimeStatus }, ct);
        return (line is null ? null : AppConfig.Deserialize<TimeAdminResponse>(line))
               ?? new TimeAdminResponse { State = RequestState.Error, Message = "Keine Antwort." };
    }

    private static async Task<string?> SendAsync(PipeMessage msg, CancellationToken ct)
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);

        var payload = new UTF8Encoding(false).GetBytes(AppConfig.SerializeCompact(msg) + "\n");
        await client.WriteAsync(payload, ct);
        await client.FlushAsync(ct);
        return await ReadLineAsync(client, ct);
    }

    private static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) break;
            if (one[0] == (byte)'\n') break;
            ms.WriteByte(one[0]);
        }
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\r');
    }
}
