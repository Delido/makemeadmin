# RunAsAdmin Server (HA-Add-on)

Zentrale Steuerung und Verlauf für die Kinder-PCs als Home-Assistant-Add-on.
Alternative zum Webhook-Modus: Die PCs reden mit **einem** Server (eigener Port),
der Freigaben verwaltet, eine Historie speichert und ein Web-Dashboard mit Graphen zeigt.

## Status
Erste lauffähige Version: API (Anfrage/Entscheidung/Polling/Events) + Web-Dashboard
(offene Anfragen, Admin-Zeiten pro Tag, meistgenutzte Programme, Verlauf).

## Installation als lokales HA-Add-on
1. Diesen Ordner `runasadmin-server/` nach `\\<HA>\addons\` kopieren
   (Samba-/SSH-Add-on), sodass `addons/runasadmin-server/config.yaml` liegt.
2. HA → Einstellungen → Add-ons → **Add-on-Store** → oben rechts ⋮ →
   „Nach Updates suchen" → unter **Lokale Add-ons** erscheint „RunAsAdmin Server".
3. Installieren → in den **Konfiguration**-Optionen `pc_token` (ein langes
   Zufallsgeheimnis) und optional `notify_service` (z. B. `mobile_app_dein_handy`)
   setzen → Starten.
4. Das Dashboard öffnet sich über das Seitenleisten-Panel (Ingress).

## API (die Kinder-PCs rufen auf)
Alle Aufrufe mit `"Token": "<pc_token>"` im JSON-Body.
- `POST /api/request`  → neue Anfrage; Antwort `{RequestId, State}`
- `POST /api/status`   → Polling; Antwort `{status: pending|allow|deny}`
- `POST /api/event`    → Ereignis melden (timeadmin.granted/revoked, …)

## PC-Dienst-Anbindung (folgt)
Im PC-Dienst wird der Modus „Server" wählbar (statt „Home Assistant"): Dann
schickt er Anfragen an `http://<ha>:8099/api/...` und pollt die Entscheidung.
