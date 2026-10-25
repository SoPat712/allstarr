import { ApiError, json, type ProviderDefinition } from "./api";
import { providerDisplayName } from "./sources";

export type ListenerContext = { backend: "Jellyfin" | "Subsonic"; username: string;
  musicClientUrl: string | null; addressSource: "configured" | "unconfigured" };
export type ReportKind = "playback" | "seek" | "wrong-match" | "wrong-lyrics" | "metadata" | "other";
export type ReportStatus = "new" | "seen" | "fixed";
export type ReportSelector = { itemId?: string; externalSnapshotId?: string;
  playback?: { subjectUserId: string; deviceId: string } };
export type ProblemContext = {
  schemaVersion: number; capturedAt: string; subjectUserId: string | null; subjectName: string | null;
  song: { itemId: string | null; externalSnapshotId: string | null; canonicalRecordingId: string | null;
    libraryTrackId: string | null; title: string | null; artist: string | null; album: string | null;
    durationMilliseconds: number | null; catalogProviderId: string | null } | null;
  match: { decisionId: string | null; state: string | null; decisionVersion: number | null;
    confidence: number | null; authorityScope: "automatic" | "personal" | "household"; reasonCodes: string[] } | null;
  playback: { providerId: string | null; sourceConfirmed: boolean; providerAccountId: string | null;
    accountRevision: number | null; openedAt: string | null; observedAt: string | null; cached: boolean | null;
    seekRung: string | null; selectionReason: string | null; positionTicks: number | null; clientName: string | null } | null;
  checks: Array<{ providerId: string; capability: string; state: string; checkedAt: string; reasonCode: string | null }>;
};
export type ProblemReport = { id: string; kind: ReportKind; status: ReportStatus; notePreview: string;
  reporterName: string; subjectName: string | null; songTitle: string | null; artist: string | null;
  providerId: string | null; clientName: string | null; createdAt: string; updatedAt: string; revision: number };
export type ProblemReportDetail = ProblemReport & { note: string; context: ProblemContext };
export type ReportReceipt = { id: string; status: ReportStatus; createdAt: string; replayed: boolean };
export type ReportSubmission = { requestId: string; kind: ReportKind; note: string; selector?: ReportSelector };

const body = (value: unknown) => ({ headers: { "Content-Type": "application/json" }, body: JSON.stringify(value) });
export const listenerSupport = {
  context: () => json<ListenerContext>("/api/admin/ui/listener-context"),
  preview: (selector?: ReportSelector) => json<ProblemContext>("/api/admin/problem-reports/context", { method: "POST", ...body(selector ?? null) }),
  create: (input: ReportSubmission) => json<ReportReceipt>("/api/admin/problem-reports", { method: "POST", ...body(input) }),
  list: (status: ReportStatus | "all", cursor?: string) => {
    const query = new URLSearchParams({ limit: "50" });
    if (status !== "all") query.set("status", status);
    if (cursor) query.set("cursor", cursor);
    return json<{ items: ProblemReport[]; nextCursor: string | null }>(`/api/admin/problem-reports?${query}`);
  },
  detail: (id: string) => json<ProblemReportDetail>(`/api/admin/problem-reports/${encodeURIComponent(id)}`),
  status: (id: string, status: ReportStatus, expectedRevision: number) =>
    json<{ id: string; status: ReportStatus; revision: number; updatedAt: string }>(
      `/api/admin/problem-reports/${encodeURIComponent(id)}/status`, { method: "PATCH", ...body({ status, expectedRevision }) }),
};

export const reportKinds: Array<{ value: ReportKind; label: string }> = [
  { value: "playback", label: "Playback" }, { value: "seek", label: "Seeking" },
  { value: "wrong-match", label: "Wrong match" }, { value: "wrong-lyrics", label: "Wrong lyrics" },
  { value: "metadata", label: "Song details" }, { value: "other", label: "Something else" },
];
export const reportStatuses: Array<{ value: ReportStatus; label: string }> = [
  { value: "new", label: "New" }, { value: "seen", label: "Seen" }, { value: "fixed", label: "Fixed" },
];
export const reportKindLabel = (kind: string) => reportKinds.find(item => item.value === kind)?.label ?? "Problem report";
export const reportStatusLabel = (status: string) => reportStatuses.find(item => item.value === status)?.label ?? "Unknown";
export const reportSourceName = (providers: ProviderDefinition[], id?: string | null) =>
  id === "jellyfin" ? "Jellyfin" : id === "subsonic" ? "Subsonic / OpenSubsonic" : providerDisplayName(providers, id ?? "", "Not captured");

export function supportError(cause: unknown, fallback: string) {
  if (cause instanceof ApiError) {
    const details = cause.details as { error?: string; message?: string } | null;
    return details?.message ?? (details?.error && /\s/.test(details.error) ? details.error : fallback);
  }
  return fallback;
}
export function supportErrorCode(cause: unknown) {
  return cause instanceof ApiError ? (cause.details as { code?: string; error?: string } | null)?.code ?? "" : "";
}
export function supportDate(value?: string | null) {
  if (!value || !Number.isFinite(Date.parse(value))) return "Not available";
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

export function musicClientUrl(value: string) {
  if (!value.trim() || /[\\\u0000-\u001f\u007f]/.test(value)) return null;
  try {
    if (/[\\\u0000-\u001f\u007f]/.test(decodeURIComponent(value))) return null;
    const url = new URL(value.trim());
    return ["http:", "https:"].includes(url.protocol) && !url.username && !url.password && !url.search && !url.hash
      ? url.toString().replace(/\/+$/, "") : null;
  } catch { return null; }
}
export function newReportRequestId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
const uuid = (value: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
export function reportSelection(query: URLSearchParams): { selector?: ReportSelector; error?: string } {
  const itemId = query.get("item") ?? "";
  const externalSnapshotId = query.get("snapshot") ?? "";
  const subjectUserId = query.get("user") ?? "";
  const deviceId = query.get("device") ?? "";
  if (!itemId && !externalSnapshotId && !subjectUserId && !deviceId) return {};
  if (Boolean(itemId) === Boolean(externalSnapshotId) || itemId.length > 500 || externalSnapshotId && !uuid(externalSnapshotId) ||
      Boolean(subjectUserId) !== Boolean(deviceId) || subjectUserId && (!itemId || !uuid(subjectUserId) || deviceId.length > 200))
    return { error: "This report link has incomplete song details. You can submit a general report instead." };
  return { selector: externalSnapshotId ? { externalSnapshotId }
    : { itemId, ...(subjectUserId ? { playback: { subjectUserId, deviceId } } : {}) } };
}
export function reportHref(selector?: ReportSelector, from = "/") {
  const query = new URLSearchParams();
  if (selector?.externalSnapshotId) query.set("snapshot", selector.externalSnapshotId);
  else if (selector?.itemId) query.set("item", selector.itemId);
  if (selector?.itemId && selector.playback) {
    query.set("user", selector.playback.subjectUserId);
    query.set("device", selector.playback.deviceId);
  }
  if (from.startsWith("/") && !from.startsWith("//")) query.set("from", from);
  return `#/report${query.size ? `?${query}` : ""}`;
}
export const reportReturnHref = (value: string) => value.startsWith("/") && !value.startsWith("//") ? `#${value}` : "#/";
