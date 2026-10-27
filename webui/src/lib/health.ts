import { ApiError, json } from "$lib/api";
import { adminUrl } from "$lib/admin-url";

export type HealthConfiguration = {
  mode: "server-api-key" | "listener";
  hasConfiguration: boolean;
  serviceCredentialReady: boolean;
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
  mode?: "server-api-key" | "listener";
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
  "enabled" | "intervalMinutes" | "searchQuery" | "firstByteBudgetSeconds"> & {
  expectedRevision: number;
  listenerUserId?: string;
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

export function healthReason(reason: string | null, mode?: HealthConfiguration["mode"]): string {
  if (!reason) return "";
  if (mode === "server-api-key") {
    const serverMessages: Record<string, string> = {
      http_401: "Jellyfin rejected the saved server API key. Review the connection in Setup.",
      http_403: "The server API key cannot read this resource. Review the server connection.",
      transport_failure: "Jellyfin could not be reached. Check the server connection.",
      invalid_response: "Jellyfin returned an unreadable response.",
      response_too_large: "The server response exceeded the check's size limit.",
      check_unavailable: "The server check could not run. Review the connection in Setup.",
    };
    if (serverMessages[reason]) return serverMessages[reason];
    const status = /^http_(\d{3})$/.exec(reason);
    if (status && !["http_429", "http_503"].includes(reason)) return `Jellyfin returned HTTP ${status[1]}.`;
  }
  const messages: Record<string, string> = {
    service_api_key_unavailable: "The saved Jellyfin server API key is unavailable. Review the connection in Setup.",
    no_library_sample: "No library audio sample is available for this check.",
    library_sample_fallback: "The query did not find a song. Another library sample was checked.",
    no_native_audio: "No library audio sample is available for this check.",
    query_no_match_fallback: "The query did not find a song. Another library sample was checked.",
    range_length_unavailable: "The sample did not supply a verified length for the seek check.",
    metadata_unavailable: "Song details must be available before this step can run.",
    backend_unavailable: "The server connection must pass before this step can run.",
    search_unavailable: "A library sample must be available before this step can run.",
    range_ignored: "The server did not return the requested byte range.",
    short_body: "The server stopped before the requested audio sample was complete.",
    empty_body: "The server returned an empty media sample.",
    artwork_unavailable: "No artwork is available for this sample.",
    lyrics_unavailable: "No lyrics are available for this sample.",
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
      not_configured: "Save health settings before running a check.",
      service_api_key_unavailable: "Set up a Jellyfin server API key in Setup before enabling checks.",
    };
    return messages[details?.code ?? details?.error ?? ""] ?? cause.message;
  }
  return "Health monitoring is unavailable. Check the connection and retry.";
}
