# RunAsAdmin Server – HA-Add-on (Architektur)

Ein Home-Assistant-Add-on als **zentrale Steuerung** für die Kinder-PCs. Alternative
zum Webhook-Modus: Die PCs reden mit **einem** Server (eigener Port), der die
Freigaben verwaltet, eine Historie speichert und ein Web-Dashboard mit Graphen zeigt.

## Warum
Der reine HA-Webhook-Weg funktioniert, ist aber fummelig (input_text-Limits,
Rückkanal-IP, keine echte Historie). Der Server-Modus bündelt alles zentral.

## Zwei Modi (im PC-Dienst wählbar)
- **Home Assistant** (wie bisher): PC ↔ HA per Webhook.
- **Server**: PC ↔ Add-on per HTTP-API (nur ausgehend vom PC → kein Firewall-Problem).

## Komponenten des Add-ons
```
runasadmin-server/
  config.yaml          # HA-Add-on-Manifest (Name, Port, Optionen)
  Dockerfile           # Python-Image
  run.sh               # Startet die App
  app/
    main.py            # FastAPI: API + Web-Dashboard
    db.py              # SQLite-Zugriff (Historie, PCs, offene Anfragen)
    models.py          # Datenmodelle
    templates/         # Dashboard-HTML (Jinja2)
    static/            # CSS/JS/Charts
```

## Datenfluss (Server-Modus)
1. **PC → Server** `POST /api/request`  → neue Admin-Anfrage (PC, User, Grund).
   Server legt sie als `pending` in die DB und schickt dir eine HA-Push
   (über den HA-Supervisor-Notify oder einen Webhook zurück an HA).
2. **Du** entscheidest (Push-Button ODER im Add-on-Dashboard) →
   `POST /api/decision` setzt die Anfrage auf `allow`/`deny`.
3. **PC pollt** `GET /api/status?request_id=...` alle 2 s → bekommt die Entscheidung.
   (Nur ausgehende Verbindung vom PC – robust, kein Rückkanal nötig.)
4. **PC meldet Ereignisse** `POST /api/event` (Admin-Zeit gestartet/beendet,
   genutzte Programme) → Server speichert sie für die Historie.

## Sicherheit
- **Token pro PC**: Jeder PC authentifiziert sich mit einem Token (in den
  Add-on-Optionen hinterlegt). Kein Kind-PC ohne gültiges Token.
- Das Add-on läuft nur im Heimnetz (HA-Ingress oder fester Port).
- Entscheidungen/Not-Aus nur über das (HA-geschützte) Dashboard.

## Datenbank (SQLite)
- `computers` (id, name, last_seen, token_hash)
- `requests` (id, computer, user, reason, state, created, decided, decided_by)
- `sessions` (id, computer, user, started, ended, apps_json)
- `events` (id, computer, ts, type, detail)

## Dashboard (Web-UI im Add-on)
- **Offene Anfragen** mit Erlauben/Ablehnen-Buttons
- **Aktive Admin-Zeiten** mit Restzeit + Not-Aus
- **Historie** pro PC/Kind (Tabelle + Filter)
- **Graphen**: Admin-Zeiten pro Tag, genutzte Programme (Top-Liste),
  Anfragen-Häufigkeit über Zeit

## Umsetzung in Schritten
1. ✅ Struktur + dieses Dokument
2. Add-on-Grundgerüst (config.yaml, Dockerfile, FastAPI „hello", DB-Schema)
3. API: /request, /decision, /status, /event + Token-Auth
4. Web-Dashboard (Anfragen, aktive Zeiten, Historie)
5. Graphen
6. PC-Dienst: Modus-Umschaltung „Server" (neuer Approver, der die API pollt)
7. HA-Push-Anbindung (Benachrichtigung bei neuer Anfrage)
