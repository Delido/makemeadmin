using System.Text.Json;

namespace RunAsAdmin.Shared;

/// <summary>
/// Protokolliert jedes Ereignis als eine JSON-Zeile (eine Datei pro Tag) und
/// zusätzlich ins Windows-Ereignisprotokoll. Die Dateien liegen unter ProgramData
/// und dürfen per ACL nur von Administratoren/SYSTEM verändert werden (Installer),
/// sodass die Kinder sie nicht löschen oder fälschen können.
/// </summary>
public sealed class AuditLog
{
    private readonly string _dir;
    private readonly object _gate = new();
    public const string EventSource = "RunAsAdmin";

    /// <summary>
    /// Optionaler Empfänger für ausgewählte Ereignisse (z. B. Meldung an Home
    /// Assistant). Wird für jedes Write mit (eventType, data) aufgerufen.
    /// </summary>
    public Action<string, object>? Sink { get; set; }

    public AuditLog(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public void Write(string eventType, object data)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.Now.ToString("o"),
            ["computer"] = Environment.MachineName,
            ["event"] = eventType,
            ["data"] = data,
        };
        var line = JsonSerializer.Serialize(entry);

        try { Sink?.Invoke(eventType, data); } catch { /* Sink darf nie stören */ }

        var file = Path.Combine(_dir, $"audit-{DateTime.Now:yyyy-MM-dd}.jsonl");
        lock (_gate)
        {
            try { File.AppendAllText(file, line + Environment.NewLine); }
            catch { /* Protokollieren darf den Dienst nie zum Absturz bringen */ }
        }

        WriteEventLog(eventType, line);
    }

    private static void WriteEventLog(string eventType, string line)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (!System.Diagnostics.EventLog.SourceExists(EventSource))
                return; // Quelle legt der Installer mit Adminrechten an.
            var type = eventType.Contains("deny", StringComparison.OrdinalIgnoreCase)
                       || eventType.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? System.Diagnostics.EventLogEntryType.Warning
                : System.Diagnostics.EventLogEntryType.Information;
            System.Diagnostics.EventLog.WriteEntry(EventSource, line, type);
        }
        catch { /* nie werfen */ }
    }
}
