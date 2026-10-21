#!/usr/bin/with-contenv bashio
# Optionen aus der Add-on-Config lesen und als Umgebungsvariablen bereitstellen.
export RAA_PC_TOKEN="$(bashio::config 'pc_token')"
export RAA_NOTIFY_SERVICE="$(bashio::config 'notify_service')"
export RAA_SUPERVISOR_TOKEN="${SUPERVISOR_TOKEN}"
export RAA_DB="/data/runasadmin.db"

cd /app
exec python3 -m uvicorn app.main:app --host 0.0.0.0 --port 8099
