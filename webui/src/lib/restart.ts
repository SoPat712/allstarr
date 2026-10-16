import type { Session } from "$lib/api";
import { adminUrl } from "$lib/admin-url";

// A pending response belongs to the old process, even if it still serves health.
export async function waitForRestart(signal?: AbortSignal): Promise<void> {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (signal?.aborted) throw new Error("Restart check cancelled.");
    await new Promise((resolve) => setTimeout(resolve, 1000));
    try {
      const attempt = AbortSignal.any([AbortSignal.timeout(5000), ...(signal ? [signal] : [])]);
      const health = await fetch(adminUrl("/health"), { cache: "no-store", signal: attempt });
      if (!health.ok) continue;
      const response = await fetch(adminUrl("/api/admin/auth/me"), { cache: "no-store", signal: attempt });
      if (!response.ok) continue;
      const session: Session = await response.json();
      if (!session.restartPending && session.backendConfigured !== false) return;
    } catch { if (signal?.aborted) throw new Error("Restart check cancelled."); }
  }
  throw new Error("Settings are saved. Allstarr has not returned yet. Check its status and reload when it is ready.");
}

// Allstarr restarts as soon as access is saved, so a dropped connection here usually means the save landed.
// The reload that follows shows the real state either way.
export function savedBeforeRestart(cause: unknown): boolean {
  return cause instanceof TypeError;
}

const restoredKey = "allstarr.access-restored";

export function markAccessRestored(): void {
  try { sessionStorage.setItem(restoredKey, "1"); } catch { /* The confirmation is optional. */ }
}

export function consumeAccessRestored(): boolean {
  try {
    const restored = sessionStorage.getItem(restoredKey) === "1";
    sessionStorage.removeItem(restoredKey);
    return restored;
  } catch { return false; }
}
