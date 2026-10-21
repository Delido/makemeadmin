using RunAsAdmin.Shared;

namespace RunAsAdmin.Service.Approval;

/// <summary>
/// Holt eine Freigabe für eine Anfrage ein. Es gibt zwei Implementierungen:
/// <see cref="HomeAssistantApprover"/> (Webhooks + Rückkanal) und
/// <see cref="ServerApprover"/> (Anfrage an das HA-Add-on, Entscheidung pollen).
/// </summary>
public interface IApprover : IAsyncDisposable
{
    /// <summary>
    /// Wird ausgelöst, wenn von außen ein Not-Aus kommt (HA: /cancel/, Server: Status „cancel").
    /// Der Worker hängt hier das Beenden der laufenden Admin-Zeit ein.
    /// </summary>
    Action? OnCancel { get; set; }

    /// <summary>Startet Hintergrundarbeit (Listener bzw. Not-Aus-Polling). Darf No-Op sein.</summary>
    void Start();

    /// <summary>Fragt die Freigabe an und wartet bis zur Entscheidung oder zum Timeout.</summary>
    Task<ApprovalDecision> AskAsync(ApprovalAsk ask, int timeoutSeconds, CancellationToken ct);
}
