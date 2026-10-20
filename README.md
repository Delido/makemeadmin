# RunAsAdmin

Kontrollierte, protokollierte Rechteerhöhung für Standardbenutzer unter Windows 11 –
ein selbstgebauter Ersatz für Tools wie „Admin By Request", zugeschnitten auf zwei
Kinder-PCs und Home Assistant.

**Idee:** Die Kinder bleiben Standardbenutzer. Ein Dienst (läuft als SYSTEM) startet
auf Anforderung **nur freigegebene Programme** erhöht – bekannte Spiele sofort,
alles andere erst nach deiner Freigabe per Handy (Home-Assistant-Push). Jede
Anforderung wird protokolliert.

---

## Bestandteile

| Teil | Projekt | Läuft wo |
|------|---------|----------|
| Dienst (nimmt Anfragen an, prüft, startet erhöht, protokolliert) | `src/RunAsAdmin.Service` | als SYSTEM auf jedem Kinder-PC |
| Kinder-Tool / CLI (`runasadmin run "Steam"`) | `src/RunAsAdmin.Client` | als das Kind auf dem Kinder-PC |
| Gemeinsames Modell, Dateiprüfung, Protokoll | `src/RunAsAdmin.Shared` | – |
| Home-Assistant-Automationen | `homeassistant/` | dein HA-Server |
| Raycast-Script-Command | `raycast/` | dein Windows-PC |

## So funktioniert eine Anfrage

1. Kind ruft `runasadmin run "Steam"` auf (Verknüpfung/Tray/Raycast).
2. Client schickt die Anfrage über eine Named Pipe an den Dienst. Der Dienst
   ermittelt den anfragenden Benutzer **aus der Pipe-Identität**, nicht aus dem Text.
3. Dienst sucht die App in der Freigabeliste und **prüft die Datei** (Pfad +
   optional Authenticode-Herausgeber und/oder SHA-256).
4. `AutoApprove: true` → sofort starten. Sonst → Anfrage per Webhook an Home
   Assistant, Push mit **Erlauben/Ablehnen**; deine Antwort geht an den lokalen
   Listener des Dienstes zurück.
5. Bei Freigabe startet der Dienst das Programm **erhöht in der Sitzung des Kindes**:
   das Konto wird dafür nur für den Start-Moment in die lokale Administratorengruppe
   genommen und sofort wieder entfernt. Das laufende Programm behält die Rechte.
6. Alles landet im Protokoll (JSON-Datei + Windows-Ereignisprotokoll + HA-Logbuch).

---

## Build & Installation (pro Kinder-PC)

Voraussetzung: .NET 8 SDK (nur zum Bauen). Auf den Kinder-PCs reicht die
.NET-8-**Runtime**.

```powershell
# 1) Veröffentlichen
.\install\Publish.ps1

# 2) Konfiguration anpassen
#    config\config.json: Programme, Pfade, HA-URL, CallbackSecret prüfen
#    (config.json wurde mit einem Zufalls-Secret aus config.example.json erzeugt)

# 3) Installieren (Administrator-PowerShell!)
.\install\Install.ps1
```

Der Installer kopiert die Dateien, legt die Konfiguration unter
`C:\ProgramData\RunAsAdmin` an, **härtet die Berechtigungen** (Kinder können
Konfiguration und Protokolle nicht ändern), erstellt die Ereignisquelle, setzt
die URL-Vorbehalte + Firewallregeln und registriert den Dienst als LocalSystem.

### Verknüpfung für die Kinder

Lege den Kindern z. B. Desktop-Verknüpfungen an:

```
"C:\Program Files\RunAsAdmin\Client\runasadmin.exe" run "Steam"
```

---

## Konfiguration (`config.json`)

```jsonc
{
  "HomeAssistant": {
    "BaseUrl": "https://dein-ha.example.com",
    "RequestWebhookId": "runasadmin_request",
    "CallbackSecret": "<langes Zufallsgeheimnis>",
    "CallbackPort": 8731          // Remote-Trigger läuft auf 8732
  },
  "AllowedApps": [
    {
      "Name": "Steam",
      "Path": "C:\\Program Files (x86)\\Steam\\steam.exe",
      "AutoApprove": true,            // sofort, nur protokolliert
      "ExpectedPublisher": "Valve"    // Datei muss gültig von Valve signiert sein
    },
    {
      "Name": "SignalRGB",
      "Path": "...SignalRgbLauncher.exe",
      "AutoApprove": false            // braucht deine Freigabe per Handy
    }
  ]
}
```

**Unbekanntes Programm nachtragen:** neuen Eintrag in `AllowedApps` ergänzen und den
Dienst neu starten (`Restart-Service RunAsAdmin`). Nur du als Admin kannst die Datei
ändern.

---

## Home Assistant

1. `homeassistant/configuration.snippet.yaml` → `rest_command` und den
   `input_text`-Helfer übernehmen.
2. `homeassistant/automation.yaml` → beide Automationen übernehmen und
   `notify.mobile_app_DEIN_HANDY` auf dein echtes Gerät setzen.
3. Webhook-ID und `CallbackSecret` müssen zur `config.json` passen.

Die Anfrage kommt per Webhook rein (nur aus dem Heimnetz, `local_only: true`),
du bekommst eine Push mit Erlauben/Ablehnen, die Antwort geht per `rest_command`
an `http://<Kinder-PC>:8731/decision/` zurück.

> Hinweis für zwei PCs: Der Zwischenspeicher `input_text.runasadmin_last` hält nur
> die *letzte* Anfrage. Kommen zwei Anfragen exakt gleichzeitig, kann das kollidieren.
> Sauberer wäre je PC ein eigener Helfer oder der Umbau auf `response_variable`.

## Raycast (dein Windows-PC)

`raycast/runasadmin-trigger.ps1` als Script Command einbinden
(Settings → Extensions → Script Commands → Add Script Directory).
Vorher einmalig das Secret setzen:

```powershell
setx RUNASADMIN_SECRET "<dasselbe CallbackSecret>"
```

Dann in Raycast „Admin-Start auf Kinder-PC" → PC + App-Name wählen. Der Befehl
ruft `http://<PC>:8732/trigger/` auf; dort greift dieselbe Freigabe-/Protokolllogik.

---

## Sicherheitshinweise (wichtig)

- **Keine vollen Admin-Sitzungen.** Es wird immer nur ein konkretes, freigegebenes
  Programm gestartet. So kann niemand in einer offenen Admin-Sitzung Protokolle
  löschen oder die Überwachung abschalten.
- **Spielordner müssen für die Kinder schreibgeschützt sein**, wenn die Programm-EXE
  erhöht gestartet wird. Sonst ließe sich die EXE gegen z. B. `cmd.exe` austauschen.
  Für Steam/Epic liegen die Launcher in `Program Files` – dort haben Standardbenutzer
  ohnehin keine Schreibrechte. Eigene/portable Programme prüfen.
- **`ExpectedPublisher`/`ExpectedSha256` setzen**, wo möglich. Ohne diese greift nur
  die Pfadprüfung; das wird im Protokoll vermerkt.
- **Secret geheim halten.** Es schützt den Rückkanal (HA → Dienst) und den
  Remote-Trigger (Raycast → Dienst). Nicht in Skripte schreiben, nur per Umgebungsvariable.
- Die Listener sind per Firewallregel auf das **private** Netzwerkprofil beschränkt.

## Protokoll ansehen

- Dateien: `C:\ProgramData\RunAsAdmin\logs\audit-JJJJ-MM-TT.jsonl` (eine JSON-Zeile je Ereignis).
- Windows-Ereignisanzeige: Anwendung, Quelle `RunAsAdmin`.
- In Home Assistant: Logbuch-Einträge der Automationen.

Protokollierte Ereignisse u. a.: `request.received`, `request.verified`,
`request.denied`, `approval.asked`, `launch.success`/`launch.failed`,
`remote.trigger`.

---

## Was noch offen ist

- **Tray-/GUI-Tool** statt Konsolen-CLI für die Kinder (bisher Verknüpfungen).
- **Kindprozess-Protokollierung**: welche weiteren Programme ein erhöht gestartetes
  Spiel selbst öffnet (z. B. über Windows-Prozessstart-Überwachung oder Sysmon).
  Derzeit protokolliert der Dienst den Start, nicht die Kindprozesse.
- **Echter Elevationstest** nur als installierter SYSTEM-Dienst möglich (die lokale
  Konsole hat das nötige `SeTcbPrivilege` nicht).
```
