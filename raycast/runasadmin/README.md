# RunAsAdmin – Raycast (Eltern-Werkzeug)

Eltern-Steuerung für [RunAsAdmin](https://github.com/Delido/makemeadmin) direkt aus Raycast
(Windows oder macOS). Spricht die Eltern-API des HA-Add-ons an.

## Befehle

- **Offene Anfragen** – offene Admin-Anfragen der Kinder beantworten: *15 Min erlauben*,
  *Immer erlauben*, *Ablehnen*.
- **Geräte-Übersicht** – pro Kinder-PC: online/offline, Client-Version, Anzahl freigegebener
  Apps, ob gerade Adminrechte aktiv sind. Aktionen: *Not-Aus* (laufende Adminrechte sofort
  beenden) und *App-Freigabe entfernen*.

## Einrichtung

In den Extension-Einstellungen eintragen:

- **Server-URL** – Basis-URL des Add-ons, ohne abschließenden `/`.
  - Über Zoraxy (auch von unterwegs): `https://runasadmin.mendyka.dns.navy`
  - Oder lokal im Heimnetz: `http://<HA-IP>:8099`
- **pc_token** – dasselbe Token wie in den Add-on-Optionen (`pc_token`).

## Sicherheit

Alle Aktionen laufen über token-geschützte Endpunkte (`/api/parent/...`). Ohne gültiges
`pc_token` antwortet das Add-on mit `403`. Das Token wird in Raycast als Passwort gespeichert.

## Entwicklung

```
npm install
npm run dev     # Extension in Raycast importieren/entwickeln
npm run build   # Produktionsbuild (ray build)
```

Benötigt **Add-on ≥ 0.11.0** (stellt die Eltern-API bereit).
