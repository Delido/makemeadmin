"""RunAsAdmin Server – FastAPI-App: API fuer die Kinder-PCs + Web-Dashboard."""
import os
import hmac
import hashlib
import time

import httpx
from fastapi import FastAPI, Request, HTTPException, Form
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from jinja2 import Environment, FileSystemLoader

from . import db

PC_TOKEN = os.environ.get("RAA_PC_TOKEN", "")
NOTIFY_SERVICE = os.environ.get("RAA_NOTIFY_SERVICE", "")
SUPERVISOR_TOKEN = os.environ.get("RAA_SUPERVISOR_TOKEN", "")
# Eigener Dashboard-Zugangsschutz (unabhaengig vom HA-Login, da auch die Kinder
# ein HA-Login haben). Leer = kein Schutz (Abwaertskompatibel).
DASHBOARD_PIN = os.environ.get("RAA_DASHBOARD_PIN", "")
# Secret zum Signieren des Login-Cookies (aus pc_token abgeleitet, stabil pro Lauf).
_COOKIE_SECRET = hashlib.sha256(("cookie:" + PC_TOKEN).encode()).hexdigest()
_COOKIE_NAME = "raa_auth"
_COOKIE_TTL = 8 * 3600   # 8 Stunden


def _make_cookie() -> str:
    exp = int(time.time()) + _COOKIE_TTL
    sig = hmac.new(_COOKIE_SECRET.encode(), str(exp).encode(), hashlib.sha256).hexdigest()
    return f"{exp}.{sig}"


def _cookie_valid(value: str | None) -> bool:
    if not value or "." not in value:
        return False
    try:
        exp_s, sig = value.rsplit(".", 1)
        if int(exp_s) < time.time():
            return False
        good = hmac.new(_COOKIE_SECRET.encode(), exp_s.encode(), hashlib.sha256).hexdigest()
        return hmac.compare_digest(sig, good)
    except Exception:
        return False


def _authed(req: Request) -> bool:
    """true, wenn kein PIN gesetzt ist ODER ein gueltiges Login-Cookie vorliegt."""
    if not DASHBOARD_PIN:
        return True
    return _cookie_valid(req.cookies.get(_COOKIE_NAME))

app = FastAPI(title="RunAsAdmin Server")
# Eigenes Jinja2-Environment (umgeht den defekten Starlette/Jinja2-Cache unter Py3.13).
_jinja = Environment(loader=FileSystemLoader("app/templates"), autoescape=True, cache_size=0)
_jinja.policies["json.dumps_kwargs"] = {"sort_keys": False}
import json as _json
_jinja.filters.setdefault("tojson", lambda v, **_: _json.dumps(v))


def _fmt_ts(ts):
    import datetime
    try:
        return datetime.datetime.fromtimestamp(int(ts)).strftime("%d.%m. %H:%M")
    except Exception:
        return str(ts)


_jinja.filters["fmt_ts"] = _fmt_ts


# Event-Typ -> (Icon, lesbarer Text). {name} wird aus dem Detail gefuellt.
_EVENT_LABELS = {
    "timeadmin.granted":        ("🔓", "Adminrechte gewährt"),
    "timeadmin.revoked":        ("🔒", "Adminrechte beendet"),
    "timeadmin.denied":         ("🚫", "Anfrage abgelehnt"),
    "timeadmin.local_pin_wrong":("⚠️", "Falsche PIN eingegeben"),
    "launch.success":           ("▶️", "{name} gestartet"),
    "game.launched":            ("🎮", "{name} gestartet"),
    "games.registered":         ("📋", "Freigegebene Apps aktualisiert"),
    "request.persisted":        ("➕", "{name} dauerhaft freigegeben"),
    "request.denied":           ("🚫", "{name} abgelehnt"),
    "app.removed":              ("🗑️", "{name} entfernt"),
    "admin.demoted":            ("⬇️", "Konto heruntergestuft"),
    "admin.permanent_warning":  ("⚠️", "Konto ist dauerhaft Admin"),
    "decision.received":        ("✉️", "Entscheidung erhalten"),
}


def _humanize(events):
    """Macht rohe Events (type+detail-JSON) zu lesbaren Zeilen fuer das Dashboard."""
    out = []
    for e in events:
        icon, text = _EVENT_LABELS.get(e.get("type", ""), ("•", e.get("type", "")))
        name = ""
        d = _parse_detail(e.get("detail"))
        if isinstance(d, dict):
            name = d.get("game") or d.get("Name") or d.get("name") or ""
        try:
            text = text.format(name=name or "Programm")
        except Exception:
            pass
        out.append({"ts": e.get("ts"), "computer": e.get("computer", "?"),
                    "icon": icon, "text": text})
    return out


def render(name: str, **ctx) -> HTMLResponse:
    return HTMLResponse(_jinja.get_template(name).render(**ctx))

db.init()


def check_token(token: str | None):
    """Konstantzeit-Vergleich des PC-Tokens."""
    if not PC_TOKEN or not token or not hmac.compare_digest(token, PC_TOKEN):
        raise HTTPException(status_code=403, detail="Ungueltiges Token")


async def ha_notify(title: str, message: str) -> tuple[bool, str]:
    """
    Push ueber den HA-Supervisor an den konfigurierten Notify-Dienst.
    Gibt (ok, detail) zurueck und schreibt bei Problemen eine sichtbare Logzeile.
    """
    if not NOTIFY_SERVICE:
        print("[notify] uebersprungen: Option 'notify_service' ist leer.", flush=True)
        return False, "notify_service ist leer (Add-on-Option setzen)."
    if not SUPERVISOR_TOKEN:
        print("[notify] FEHLER: SUPERVISOR_TOKEN fehlt (homeassistant_api?).", flush=True)
        return False, "SUPERVISOR_TOKEN fehlt."
    # Der Wert soll OHNE 'notify.'-Praefix sein; falls doch gesetzt, tolerant abschneiden.
    svc = NOTIFY_SERVICE
    if svc.startswith("notify."):
        svc = svc[len("notify."):]
    url = f"http://supervisor/core/api/services/notify/{svc}"
    headers = {"Authorization": f"Bearer {SUPERVISOR_TOKEN}"}
    try:
        async with httpx.AsyncClient(timeout=10) as client:
            r = await client.post(url, headers=headers,
                                  json={"title": title, "message": message})
        if r.status_code < 300:
            print(f"[notify] gesendet an '{svc}' ({r.status_code}).", flush=True)
            return True, f"gesendet ({r.status_code})"
        body = (r.text or "")[:200]
        print(f"[notify] FEHLER {r.status_code} an '{svc}': {body}", flush=True)
        return False, f"HA antwortete {r.status_code}: {body}"
    except Exception as ex:
        print(f"[notify] AUSNAHME an '{svc}': {ex}", flush=True)
        return False, str(ex)


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


@app.post("/api/cancel-status")
async def api_cancel_status(req: Request):
    """Der PC-Dienst pollt, ob ein Not-Aus fuer ihn ansteht (einmalig abgeholt)."""
    data = await req.json()
    check_token(data.get("Token"))
    computer = data.get("Computer", "?")
    db.seen(computer)
    return {"cancel": db.take_cancel(computer)}


@app.post("/api/remove-status")
async def api_remove_status(req: Request):
    """Der PC-Dienst pollt, welche App-Freigaben entfernt werden sollen (Namen)."""
    data = await req.json()
    check_token(data.get("Token"))
    computer = data.get("Computer", "?")
    db.seen(computer)
    return {"remove": db.take_removals(computer)}


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
    elif etype == "game.launched":
        d = _parse_detail(detail)
        db.add_game_launch(computer, d.get("user") or data.get("User", "?"),
                           d.get("game") or "?")
    elif etype == "games.registered":
        d = _parse_detail(detail)
        db.set_registered_games(computer, d.get("games") or [])
    return {"ok": True}


def _parse_detail(detail):
    """Detail ist ein JSON-String (vom PC-Dienst). Robust zu dict parsen."""
    if isinstance(detail, dict):
        return detail
    try:
        v = _json.loads(detail) if detail else {}
        return v if isinstance(v, dict) else {}
    except Exception:
        return {}


# ---------------- Web-Dashboard (HA-Ingress) ----------------

@app.get("/login", response_class=HTMLResponse)
async def login_page(request: Request, fehler: str = ""):
    return render("login.html", fehler=fehler)


@app.post("/login")
async def login(request: Request, pin: str = Form("")):
    if DASHBOARD_PIN and hmac.compare_digest(pin, DASHBOARD_PIN):
        resp = RedirectResponse("./", status_code=303)
        resp.set_cookie(_COOKIE_NAME, _make_cookie(), max_age=_COOKIE_TTL,
                        httponly=True, samesite="lax")
        return resp
    return RedirectResponse("login?fehler=1", status_code=303)


@app.get("/", response_class=HTMLResponse)
async def dashboard(request: Request):
    # Eigener Zugangsschutz: Kinder haben zwar ein HA-Login, aber ohne PIN kein
    # Zugriff auf dieses Dashboard.
    if not _authed(request):
        return RedirectResponse("login", status_code=303)
    return render("dashboard.html",
        pending=db.pending_requests(),
        active=db.active_sessions(),
        events=_humanize(db.recent_events(40)),
        per_day=db.sessions_per_day(),
        top_apps=db.top_apps(),
        top_games=db.top_games(),
        reg_games=db.registered_games(),
        notify_status=request.query_params.get("notify"),
        notify_detail=request.query_params.get("detail", ""),
        notify_configured=bool(NOTIFY_SERVICE),
    )


@app.post("/decide")
async def decide(request: Request, request_id: str = Form(...), action: str = Form(...)):
    if not _authed(request):
        raise HTTPException(status_code=403, detail="Nicht angemeldet")
    # allow_always = dauerhaft erlauben (Client traegt das Programm dann fest ein).
    if action == "allow_always":
        state = "allow_always"
    elif action == "allow":
        state = "allow"
    else:
        state = "deny"
    db.decide(request_id, state, "dashboard")
    return RedirectResponse("./", status_code=303)


@app.post("/cancel")
async def cancel(request: Request, computer: str = Form(...)):
    """Not-Aus vom Dashboard: der betroffene PC beendet die Admin-Zeit beim naechsten Poll."""
    if not _authed(request):
        raise HTTPException(status_code=403, detail="Nicht angemeldet")
    db.request_cancel(computer)
    return RedirectResponse("./", status_code=303)


@app.post("/remove-game")
async def remove_game(request: Request, computer: str = Form(...), game: str = Form(...)):
    """App-Freigabe vom Dashboard entfernen: der PC holt das beim naechsten Poll ab."""
    if not _authed(request):
        raise HTTPException(status_code=403, detail="Nicht angemeldet")
    db.request_remove(computer, game)
    return RedirectResponse("./", status_code=303)


@app.post("/test-notify")
async def test_notify(request: Request):
    """Schickt eine Test-Push, um die notify_service-Einstellung zu pruefen."""
    if not _authed(request):
        raise HTTPException(status_code=403, detail="Nicht angemeldet")
    ok, detail = await ha_notify("RunAsAdmin – Test",
                                 "Test-Benachrichtigung vom RunAsAdmin-Add-on. Wenn du das liest, funktioniert es! ✅")
    return RedirectResponse(f"./?notify={'ok' if ok else 'err'}&detail={detail[:160]}", status_code=303)


@app.get("/api/health")
async def health():
    return JSONResponse({"ok": True, "token_set": bool(PC_TOKEN)})
