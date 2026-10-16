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
