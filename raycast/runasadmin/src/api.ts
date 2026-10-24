import { getPreferenceValues } from "@raycast/api";

export interface Device {
  name: string;
  version: string;
  online: boolean;
  last_seen_text: string;
  apps: string[];
  admin_active: boolean;
}

export interface PendingRequest {
  id: string;
  computer: string;
  user: string;
  reason: string | null;
  created: number;
}

export interface ActiveSession {
  computer: string;
  user: string;
  started: number;
}

export interface Overview {
  devices: Device[];
  pending: PendingRequest[];
  active: ActiveSession[];
}

export type Decision = "allow" | "allow_always" | "deny";

interface Prefs {
  serverUrl: string;
  pcToken: string;
}

function prefs(): Prefs {
  const p = getPreferenceValues<Prefs>();
  return { serverUrl: (p.serverUrl || "").replace(/\/+$/, ""), pcToken: p.pcToken || "" };
}

/** Basis-URL fuer useFetch (GET /api/parent/overview). */
export function overviewUrl(): string {
  return `${prefs().serverUrl}/api/parent/overview`;
}

/** Header fuer die GET-Abfrage (Token im Header). */
export function authHeaders(): Record<string, string> {
  return { "X-RAA-Token": prefs().pcToken };
}

async function post(path: string, body: Record<string, unknown>): Promise<void> {
  const { serverUrl, pcToken } = prefs();
  const resp = await fetch(`${serverUrl}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ Token: pcToken, ...body }),
  });
  if (!resp.ok) {
    let detail = `HTTP ${resp.status}`;
    try {
      const j = (await resp.json()) as { detail?: string };
      if (j?.detail) detail = j.detail;
    } catch {
      // ignore
    }
    throw new Error(detail);
  }
}

export async function decide(requestId: string, action: Decision): Promise<void> {
  await post("/api/parent/decide", { RequestId: requestId, Action: action });
}

export async function cancel(computer: string): Promise<void> {
  await post("/api/parent/cancel", { Computer: computer });
}

export async function removeApp(computer: string, game: string): Promise<void> {
  await post("/api/parent/remove", { Computer: computer, Game: game });
}
