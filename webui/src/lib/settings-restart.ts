import type { OidcAccess } from "$lib/api";

export type RestartUndo = { kind: "config"; key: string; value: string; label: string }
  | { kind: "oidc"; settings: Pick<OidcAccess, "enabled" | "authority" | "clientId" | "publicUrl" | "displayName">; label: string };
const storageKey = "allstarr.settings.restart-undo";

export function rememberRestartUndo(value: RestartUndo) {
  try { sessionStorage.setItem(storageKey, JSON.stringify(value)); } catch { /* Settings remain usable without browser storage. */ }
}

export function takeRestartUndo(): RestartUndo | null {
  try {
    const raw = sessionStorage.getItem(storageKey);
    sessionStorage.removeItem(storageKey);
    const value = JSON.parse(raw ?? "null");
    return value && ["config", "oidc"].includes(value.kind) && typeof value.label === "string" ? value : null;
  } catch { return null; }
}
