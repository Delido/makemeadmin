"""SQLite-Zugriff fuer den RunAsAdmin-Server."""
import os
import sqlite3
import time
from contextlib import contextmanager

DB_PATH = os.environ.get("RAA_DB", "/data/runasadmin.db")

SCHEMA = """
CREATE TABLE IF NOT EXISTS computers (
    name TEXT PRIMARY KEY,
    last_seen INTEGER
);
CREATE TABLE IF NOT EXISTS requests (
    id TEXT PRIMARY KEY,
    computer TEXT,
    user TEXT,
    reason TEXT,
    state TEXT,            -- pending | allow | deny
    created INTEGER,
    decided INTEGER,
    decided_by TEXT
);
CREATE TABLE IF NOT EXISTS sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    computer TEXT,
    user TEXT,
    started INTEGER,
    ended INTEGER,
    apps TEXT             -- Komma-Liste genutzter Admin-Programme
);
CREATE TABLE IF NOT EXISTS events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    computer TEXT,
    ts INTEGER,
    type TEXT,
    detail TEXT
);
CREATE TABLE IF NOT EXISTS cancels (
    computer TEXT PRIMARY KEY,   -- offener Not-Aus; vom PC beim Pollen abgeholt
    requested INTEGER
);
CREATE TABLE IF NOT EXISTS removals (
    computer TEXT,               -- offene App-Entfernung; vom PC abgeholt
    game TEXT,
    requested INTEGER,
    PRIMARY KEY (computer, game)
);
CREATE TABLE IF NOT EXISTS game_launches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    computer TEXT,
    user TEXT,
    game TEXT,
    ts INTEGER
);
CREATE TABLE IF NOT EXISTS registered_games (
    computer TEXT,               -- pro PC die aktuell freigegebenen Spiele
    game TEXT,
    PRIMARY KEY (computer, game)
);
"""


@contextmanager
def conn():
    c = sqlite3.connect(DB_PATH)
    c.row_factory = sqlite3.Row
    try:
        yield c
        c.commit()
    finally:
        c.close()


def init():
    os.makedirs(os.path.dirname(DB_PATH), exist_ok=True)
    with conn() as c:
        c.executescript(SCHEMA)


def seen(computer: str):
    with conn() as c:
        c.execute(
            "INSERT INTO computers(name,last_seen) VALUES(?,?) "
            "ON CONFLICT(name) DO UPDATE SET last_seen=excluded.last_seen",
            (computer, int(time.time())),
        )


def add_request(rid, computer, user, reason):
    with conn() as c:
        c.execute(
            "INSERT OR REPLACE INTO requests(id,computer,user,reason,state,created) "
            "VALUES(?,?,?,?, 'pending', ?)",
            (rid, computer, user, reason, int(time.time())),
        )


def decide(rid, state, by):
    with conn() as c:
        c.execute(
            "UPDATE requests SET state=?, decided=?, decided_by=? WHERE id=?",
            (state, int(time.time()), by, rid),
        )


def get_request(rid):
    with conn() as c:
        r = c.execute("SELECT * FROM requests WHERE id=?", (rid,)).fetchone()
        return dict(r) if r else None


def pending_requests():
    with conn() as c:
        rows = c.execute(
            "SELECT * FROM requests WHERE state='pending' ORDER BY created DESC"
        ).fetchall()
        return [dict(r) for r in rows]


def add_event(computer, type_, detail):
    with conn() as c:
        c.execute(
            "INSERT INTO events(computer,ts,type,detail) VALUES(?,?,?,?)",
            (computer, int(time.time()), type_, detail),
        )


def start_session(computer, user):
    with conn() as c:
        cur = c.execute(
            "INSERT INTO sessions(computer,user,started) VALUES(?,?,?)",
            (computer, user, int(time.time())),
        )
        return cur.lastrowid


def end_session(computer, apps):
    with conn() as c:
        row = c.execute(
            "SELECT id FROM sessions WHERE computer=? AND ended IS NULL "
            "ORDER BY started DESC LIMIT 1",
            (computer,),
        ).fetchone()
        if row:
            c.execute(
                "UPDATE sessions SET ended=?, apps=? WHERE id=?",
                (int(time.time()), apps, row["id"]),
            )


def request_cancel(computer):
    """Setzt einen Not-Aus fuer einen PC (vom Dashboard ausgeloest)."""
    with conn() as c:
        c.execute(
            "INSERT INTO cancels(computer,requested) VALUES(?,?) "
            "ON CONFLICT(computer) DO UPDATE SET requested=excluded.requested",
            (computer, int(time.time())),
        )


def take_cancel(computer):
    """Holt einen offenen Not-Aus ab und loescht ihn (einmalig). True = es lag einer vor."""
    with conn() as c:
        row = c.execute(
            "SELECT computer FROM cancels WHERE computer=?", (computer,)
        ).fetchone()
        if row:
            c.execute("DELETE FROM cancels WHERE computer=?", (computer,))
            return True
    return False


def request_remove(computer, game):
    """Dashboard moechte eine App-Freigabe auf einem PC entfernen."""
    with conn() as c:
        c.execute(
            "INSERT INTO removals(computer,game,requested) VALUES(?,?,?) "
            "ON CONFLICT(computer,game) DO UPDATE SET requested=excluded.requested",
            (computer, game, int(time.time())),
        )


def take_removals(computer):
    """Holt offene Entfernungs-Wuensche eines PCs ab und loescht sie (Liste der Namen)."""
    with conn() as c:
        rows = c.execute(
            "SELECT game FROM removals WHERE computer=?", (computer,)
        ).fetchall()
        names = [r["game"] for r in rows]
        if names:
            c.execute("DELETE FROM removals WHERE computer=?", (computer,))
    return names


def active_sessions():
    """Laufende Admin-Zeiten (noch nicht beendet) fuer die Dashboard-Anzeige."""
    with conn() as c:
        rows = c.execute(
            "SELECT * FROM sessions WHERE ended IS NULL ORDER BY started DESC"
        ).fetchall()
        return [dict(r) for r in rows]


def add_game_launch(computer, user, game):
    with conn() as c:
        c.execute(
            "INSERT INTO game_launches(computer,user,game,ts) VALUES(?,?,?,?)",
            (computer, user, game, int(time.time())),
        )


def set_registered_games(computer, games):
    """Ersetzt die Liste der freigegebenen Spiele eines PCs komplett."""
    with conn() as c:
        c.execute("DELETE FROM registered_games WHERE computer=?", (computer,))
        for g in games:
            g = (g or "").strip()
            if g:
                c.execute(
                    "INSERT OR IGNORE INTO registered_games(computer,game) VALUES(?,?)",
                    (computer, g),
                )


def registered_games():
    """Freigegebene Spiele gruppiert nach PC."""
    with conn() as c:
        rows = c.execute(
            "SELECT computer, game FROM registered_games ORDER BY computer, game"
        ).fetchall()
    out = {}
    for r in rows:
        out.setdefault(r["computer"], []).append(r["game"])
    return out


def top_games(days=30, limit=10):
    with conn() as c:
        rows = c.execute(
            "SELECT game, COUNT(*) AS n FROM game_launches "
            "WHERE ts > ? GROUP BY game ORDER BY n DESC LIMIT ?",
            (int(time.time()) - days * 86400, limit),
        ).fetchall()
        return [(r["game"], r["n"]) for r in rows]


def recent_events(limit=100):
    with conn() as c:
        rows = c.execute(
            "SELECT * FROM events ORDER BY ts DESC LIMIT ?", (limit,)
        ).fetchall()
        return [dict(r) for r in rows]


def sessions_per_day(days=14):
    with conn() as c:
        rows = c.execute(
            "SELECT date(started,'unixepoch','localtime') AS day, COUNT(*) AS n "
            "FROM sessions WHERE started > ? GROUP BY day ORDER BY day",
            (int(time.time()) - days * 86400,),
        ).fetchall()
        return [dict(r) for r in rows]


def top_apps(days=30, limit=10):
    counts = {}
    with conn() as c:
        rows = c.execute(
            "SELECT apps FROM sessions WHERE apps IS NOT NULL AND started > ?",
            (int(time.time()) - days * 86400,),
        ).fetchall()
    for r in rows:
        for a in (r["apps"] or "").split(","):
            a = a.strip()
            if a:
                counts[a] = counts.get(a, 0) + 1
    return sorted(counts.items(), key=lambda kv: kv[1], reverse=True)[:limit]
