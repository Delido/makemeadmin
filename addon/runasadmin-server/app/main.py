"""RunAsAdmin Server – FastAPI-App: API fuer die Kinder-PCs + Web-Dashboard."""
import os
import hmac

import httpx
from fastapi import FastAPI, Request, HTTPException, Form
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
from jinja2 import Environment, FileSystemLoader

from . import db

PC_TOKEN = os.environ.get("RAA_PC_TOKEN", "")
NOTIFY_SERVICE = os.environ.get("RAA_NOTIFY_SERVICE", "")
SUPERVISOR_TOKEN = os.environ.get("RAA_SUPERVISOR_TOKEN", "")

app = FastAPI(title="RunAsAdmin Server")
# Eigenes Jinja2-Environment (umgeht den defekten Starlette/Jinja2-Cache unter Py3.13).
_jinja = Environment(loader=FileSystemLoader("app/templates"), autoescape=True, cache_size=0)
_jinja.policies["json.dumps_kwargs"] = {"sort_keys": False}
import json as _json
_jinja.filters.setdefault("tojson", lambda v, **_: _json.dumps(v))
app.mount("/static", StaticFiles(directory="app/static"), name="static")


def _fmt_ts(ts):
    import datetime
    try:
        return datetime.datetime.fromtimestamp(int(ts)).strftime("%d.%m. %H:%M")
    except Exception:
        return str(ts)


_jinja.filters["fmt_ts"] = _fmt_ts


def render(name: str, **ctx) -> HTMLResponse:
    return HTMLResponse(_jinja.get_template(name).render(**ctx))

db.init()


def check_token(token: str | None):
    """Konstantzeit-Vergleich des PC-Tokens."""
    if not PC_TOKEN or not token or not hmac.compare_digest(token, PC_TOKEN):
        raise HTTPException(status_code=403, detail="Ungueltiges Token")


async def ha_notify(title: str, message: str):
    """Push ueber den HA-Supervisor an den konfigurierten Notify-Dienst."""
    if not (NOTIFY_SERVICE and SUPERVISOR_TOKEN):
        return
    url = f"http://supervisor/core/api/services/notify/{NOTIFY_SERVICE}"
    headers = {"Authorization": f"Bearer {SUPERVISOR_TOKEN}"}
    try:
        async with httpx.AsyncClient(timeout=10) as client:
            await client.post(url, headers=headers,
                              json={"title": title, "message": message})
    except Exception:
        pass


# ---------------- API fuer die Kinder-PCs ----------------

@app.post("/api/request")
async def api_request(req: Request):
    data = await req.json()
    check_token(data.get("Token"))
    computer = data.get("Computer", "?")
    user = data.get("User", "?")
    rid = data.get("RequestId") or os.urandom(8).hex()
    reason = data.get("Reason") or ""
    db.seen(computer)
    db.add_request(rid, computer, user, reason)
    await ha_notify("Adminrechte angefragt",
                    f"{user} an {computer} möchte 15 Minuten Admin. "
                    f"Im RunAsAdmin-Dashboard erlauben/ablehnen.")
    return {"RequestId": rid, "State": "pending"}


@app.post("/api/status")
async def api_status(req: Request):
    data = await req.json()
    check_token(data.get("Token"))
    r = db.get_request(data.get("RequestId", ""))
    if not r:
        return {"status": "unknown"}
    return {"status": r["state"]}  # pending | allow | deny


@app.post("/api/event")
async def api_event(req: Request):
    data = await req.json()
    check_token(data.get("Token"))
    computer = data.get("Computer", "?")
    etype = data.get("Type", "")
    detail = data.get("Detail", "")
    db.seen(computer)
    db.add_event(computer, etype, detail)
    if etype == "timeadmin.granted":
        db.start_session(computer, data.get("User", "?"))
    elif etype == "timeadmin.revoked":
        db.end_session(computer, data.get("Apps", ""))
    return {"ok": True}


# ---------------- Web-Dashboard (HA-Ingress) ----------------

@app.get("/", response_class=HTMLResponse)
async def dashboard(request: Request):
    return render("dashboard.html",
        pending=db.pending_requests(),
        events=db.recent_events(60),
        per_day=db.sessions_per_day(),
        top_apps=db.top_apps(),
    )


@app.post("/decide")
async def decide(request_id: str = Form(...), action: str = Form(...)):
    db.decide(request_id, "allow" if action == "allow" else "deny", "dashboard")
    return RedirectResponse("/", status_code=303)


@app.get("/api/health")
async def health():
    return JSONResponse({"ok": True, "token_set": bool(PC_TOKEN)})
