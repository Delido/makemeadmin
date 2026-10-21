# RunAsAdmin

Kontrollierte, protokollierte **Adminrechte auf Zeit** für Standardbenutzer unter
Windows 11 – ein selbstgebauter Ersatz für Tools wie „Admin By Request", gekoppelt
an Home Assistant. Gedacht für Kinder-PCs: Die Kinder bleiben normale Benutzer und
können bei Bedarf **15 Minuten Adminrechte anfragen**, die ein Elternteil per Handy
freigibt. Danach werden die Rechte automatisch wieder entzogen – und es wird
protokolliert, welche Programme in der Zeit mit Adminrechten liefen.

> **Prinzip (MakeMeAdmin):** Statt einzelne Programme erhöht zu starten, wird das
> Kind nach der Freigabe kurz **echtes Admin-Mitglied**. Es startet seine Programme
> dann selbst (bei Bedarf mit einem UAC-Klick, den es nun bestätigen darf). Nach
> Ablauf der Zeit wird es automatisch zurückgestuft.

![RunAsAdmin – die App auf dem Kinder-PC](assets/screenshot.png)

Die App auf dem Kinder-PC: Seitenleiste (Start · Verlauf · Status · Eltern),
Statusanzeige und der Knopf „Adminrechte anfragen (15 Min)".

---

## Ablauf

1. Kind öffnet die **RunAsAdmin-App** (Tray-Symbol / Startmenü) und klickt
   **„Adminrechte anfragen (15 Min)"**.
2. Der Dienst schickt die Anfrage per Webhook an **Home Assistant** → du bekommst
   eine **Push-Benachrichtigung** mit *Erlauben / Ablehnen*.
3. Bei *Erlauben* nimmt der Dienst (läuft als SYSTEM) das Kind in die lokale
   Administratorengruppe. Die App zeigt **„Du bist 15 Minuten Admin"** + Restzeit,
   das Tray-Symbol wird grün.
4. Das Kind startet seine Programme (Spiele, Updater …) **selbst**.
5. Während der Zeit protokolliert der Dienst, welche Programme **tatsächlich
   erhöht** (als Admin) liefen.
6. Nach 15 Minuten: automatischer Entzug der Adminrechte. In Home Assistant
   erscheint im Logbuch z. B. *„Admin-Zeit beendet – genutzt: Steam, …"*.

---

## Bestandteile

| Teil | Projekt | Läuft wo |
|------|---------|----------|
| **Dienst** (nimmt Anfragen an, verwaltet Admin-Zeit, protokolliert) | `src/RunAsAdmin.Service` | als SYSTEM auf jedem Kinder-PC |
| **GUI** (Tray + Fenster: anfragen, Status, Verlauf, Einstellungen) | `src/RunAsAdmin.Gui` | als das Kind auf dem Kinder-PC |
| **Installer** (Single-File-Setup, Deinstaller, Cleanup) | `src/RunAsAdmin.Installer` | einmal pro PC |
| CLI (optional, für Skripte) | `src/RunAsAdmin.Client` | – |
| Gemeinsames Modell, Verschlüsselung, Update | `src/RunAsAdmin.Shared` | – |
| **Home Assistant** (Automationen, Dashboard) | `homeassistant/` | dein HA-Server |

---

## Installation (pro Kinder-PC)

Die Release-Dateien sind **self-contained** – es ist **kein** .NET nötig.

1. Aktuelles **`RunAsAdmin-Setup.zip`** von den
   [Releases](https://github.com/Delido/makemeadmin/releases) herunterladen und
   entpacken.
2. **`RunAsAdmin-Setup.exe`** (auf der obersten Ebene) als Administrator ausführen.
   Der Installer kopiert die Dateien, härtet die Berechtigungen (Kinder können
   Konfiguration und Protokolle nicht ändern), legt das Tray-Autostart an,
   registriert den Dienst als LocalSystem und setzt URL-Vorbehalte + Firewallregeln.
3. **Einrichten (einmalig):** Tray-Symbol → *Einstellungen (Eltern)*:
   Home-Assistant-Adresse, das Geheimnis (CallbackSecret) und eine **Eltern-PIN**
   eintragen, „Home Assistant verwenden" anhaken.

> Bei Problemen mit einer alten Installation: `Cleanup.ps1` (im ZIP) als Admin
> ausführen, ggf. neu starten, dann Setup erneut.

**Updates:** Die App prüft stündlich selbst auf neue Releases und spielt sie ein
(nicht während einer aktiven Admin-Zeit). Manuell: in der App unter *Status* →
„Nach Updates suchen".

---

## Sicherheit

- **Geheimnisse verschlüsselt:** Das `CallbackSecret` wird mit Windows **DPAPI**
  verschlüsselt gespeichert, nie im Klartext. Die Eltern-**PIN** liegt als
  gesalzener Hash vor. Beides ist nur über die PIN-geschützten Einstellungen änderbar.
- **Dieses Repo enthält keine persönlichen Daten** – keine HA-Adresse, kein Secret.
  Du trägst diese nur lokal in der App ein.
- **Gehärtete Rechte:** Konfiguration (`C:\ProgramData\RunAsAdmin\config.json`) und
  Protokolle sind per ACL für die Kinder gesperrt.
- **Zeitlich begrenzt:** Adminrechte werden nach 15 Minuten automatisch entzogen –
  auch nach einem Dienst-Neustart wird eine verwaiste Mitgliedschaft aufgeräumt.
- Die lokalen Listener sind per Firewallregel beschränkt.

---

## Home Assistant

Im Ordner `homeassistant/`:

1. **`configuration.snippet.yaml`** → `rest_command`, die `input_text`-Helfer und
   `counter` übernehmen → **HA neu starten**.
2. **`automation.yaml`** → die Automationen in `automations.yaml` übernehmen und
   `notify.mobile_app_DEIN_HANDY` auf dein Gerät setzen → Automationen neu laden.
3. **`dashboard.yaml`** → als neues Dashboard über den Raw-Editor einfügen
   (Status, Statistik, Verlauf „wer / wann / was").

Die Anfrage kommt per Webhook rein (nur aus dem Heimnetz, `local_only: true`), die
Push bietet *Erlauben / Ablehnen*, die Entscheidung geht an den Rückkanal des PCs
zurück (der Dienst nutzt dafür seine lokale IP). Ereignisse (Freigabe, Ablauf mit
genutzten Programmen) landen im HA-Logbuch und in Zählern.

---

## Protokoll ansehen

- **Lokal (fälschungssicher):** `C:\ProgramData\RunAsAdmin\logs\audit-JJJJ-MM-TT.jsonl`
  (eine JSON-Zeile je Ereignis) + Windows-Ereignisanzeige (Quelle `RunAsAdmin`).
- **In Home Assistant:** Logbuch + Dashboard.

Wichtige Ereignisse: `timeadmin.granted`, `timeadmin.process` (nur erhöhte
Programme), `timeadmin.revoked` (mit Programmliste), `timeadmin.denied`,
`settings.saved`, `update.*`.

---

## Selbst bauen

Voraussetzung: .NET 8 SDK.

```powershell
.\install\Publish.ps1    # baut Service/Client/GUI/Installer self-contained nach .\publish
```

Die Versionsnummer wird zentral in `Directory.Build.props` gepflegt (gilt für alle
Projekte, damit die Dateiversion jeder exe zum Release passt).

---

## Hinweise / Grenzen

- **Ein UAC-Klick** kann je nach Programm nötig bleiben – das Kind kann ihn in der
  Admin-Zeit aber selbst bestätigen (es braucht kein Eltern-Passwort).
- Die „erhöht gelaufen?"-Erkennung fängt Programme, die ein Admin-Token haben;
  je nach Spiel/Launcher kann die Liste nachjustiert werden.
