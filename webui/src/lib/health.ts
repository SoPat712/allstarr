import { ApiError, json } from "$lib/api";
import { adminUrl } from "$lib/admin-url";

export type HealthConfiguration = {
  enabled: boolean;
  intervalMinutes: number;
  listenerUserId: string | null;
  searchQuery: string;
  firstByteBudgetSeconds: number;
  revision: number;
  hasListenerCredential: boolean;
  hasWebhook: boolean;
  nextRunAt: string | null;
};

export type HealthStep = {
  name: string;
  status: "passed" | "failed" | "skipped";
  latencyMilliseconds: number | null;
  reason: string | null;
};

export type HealthRun = {
  id: string;
  protocol: string;
  startedAt: string;
  completedAt: string | null;
  passed: boolean;
  steps: HealthStep[];
  alertStatus: "none" | "not_configured" | "pending" | "delivered" | "failed";
};

export type HealthSnapshot = {
  configuration: HealthConfiguration;
  protocol: string;
  inactiveProtocol: string;
  listeners: Array<{ id: string; displayName: string; protocol: string; isSynthetic: boolean }>;
  runs: HealthRun[];
  runningJobId: string | null;
};

export type HealthUpdate = Pick<HealthConfiguration,
  "enabled" | "listenerUserId" | "intervalMinutes" | "searchQuery" | "firstByteBudgetSeconds"> & {
  expectedRevision: number;
  username?: string;
  password?: string;
  webhookUrl?: string;
  clearWebhook: boolean;
};

export const health = {
  get: (signal?: AbortSignal) => json<HealthSnapshot>(adminUrl("/api/admin/health"), { signal }),
  save: (value: HealthUpdate, signal?: AbortSignal) => json<HealthSnapshot>(adminUrl("/api/admin/health"), {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value), signal,
  }),
  run: (signal?: AbortSignal) => json<{ jobId: string }>(adminUrl("/api/admin/health/run"), {
    method: "POST", signal,
  }),
};

export function healthReason(reason: string | null): string {
  if (!reason) return "";
  const messages: Record<string, string> = {
    no_eligible_song: "No eligible provider song was found. Check the query and automatic sources.",
    search_required: "Search must pass before this step can run.",
    playlist_required: "A temporary playlist must be created before this step can run.",
    heart_required: "The original favorite state could not be read.",
    range_unavailable: "The source did not supply a length for the seek check.",
    invalid_range: "The source did not return the requested byte range.",
    invalid_audio: "The source returned an unusable audio response.",
    truncated_audio: "The source stopped before the audio sample was complete.",
    lyrics_missing: "No lyrics were returned for this song.",
    favorite_state_mismatch: "The saved favorite state did not match the requested change.",
    playlist_id_missing: "The listener API did not return the new playlist's identity.",
    playlist_entry_missing: "The temporary playlist track could not be found for removal.",
    playlist_state_mismatch: "The temporary playlist did not reflect the requested change.",
    cleanup_incomplete: "Temporary items still need cleanup. Check the dedicated listener's access.",
    cleanup_scope_invalid: "The saved temporary items could not be verified for cleanup.",
    timeout: "The step exceeded its time limit.",
    check_timeout: "The health check exceeded its overall time limit.",
    transport_failure: "The listener API could not be reached.",
    invalid_response: "The listener API returned an unreadable response.",
    response_too_large: "The listener API response exceeded the check's size limit.",
    protocol_error: "The listener API rejected the operation.",
    protocol_error_70: "The requested operation is unavailable on this listener API.",
    check_unavailable: "The check could not run. Verify the dedicated listener and its settings.",
    run_interrupted: "This run was interrupted. Temporary items were checked for cleanup.",
    http_401: "The listener sign-in expired. Verify its credentials again.",
    http_403: "The dedicated listener does not have permission for this operation.",
    http_429: "The request was rate limited. Wait before running another check.",
    http_503: "The service is temporarily unavailable. Try again later.",
  };
  if (messages[reason]) return messages[reason];
  const status = /^http_(\d{3})$/.exec(reason);
  return status ? `The listener API returned HTTP ${status[1]}.` : "The check could not complete. Run it again and review source health.";
}

export function healthError(cause: unknown): string {
  if (cause instanceof ApiError) {
    const details = cause.details as { code?: string; error?: string } | null;
    const messages: Record<string, string> = {
      revision_conflict: "Health settings changed. Reload settings and try again.",
      check_running: "A health check is already running. Refresh results to follow it.",
      not_configured: "Configure and save a listener before running a check.",
    };
    return messages[details?.code ?? details?.error ?? ""] ?? cause.message;
  }
  return "Health monitoring is unavailable. Check the connection and retry.";
}
