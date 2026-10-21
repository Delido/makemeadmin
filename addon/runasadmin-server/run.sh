#!/usr/bin/env bash
# Add-on-Optionen aus /data/options.json lesen und als Umgebungsvariablen setzen.
set -e

OPTIONS=/data/options.json

export RAA_PC_TOKEN="$(jq -r '.pc_token // ""' "$OPTIONS")"
export RAA_NOTIFY_SERVICE="$(jq -r '.notify_service // ""' "$OPTIONS")"
export RAA_DASHBOARD_PIN="$(jq -r '.dashboard_pin // ""' "$OPTIONS")"
export RAA_SUPERVISOR_TOKEN="${SUPERVISOR_TOKEN}"
export RAA_DB="/data/runasadmin.db"

cd /app
exec python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8099
