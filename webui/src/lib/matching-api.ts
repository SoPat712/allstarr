import {
  json,
  type ManualMatchAuthority,
  type TrackRematchAllPreview,
} from "$lib/api";

const authorityPath = (authority: ManualMatchAuthority) =>
  `/api/admin/track-matches/${encodeURIComponent(authority.authoritySnapshotId)}` +
  `/manual-authorities/${encodeURIComponent(authority.id)}`;

export const manualMatchAuthority = {
  delete: (authority: ManualMatchAuthority) => {
    const query = new URLSearchParams({
      kind: authority.kind,
      expectedRevision: String(authority.revision),
    });
    return json<void>(`${authorityPath(authority)}?${query}`, { method: "DELETE" });
  },
  rematch: (authority: ManualMatchAuthority) =>
    json<{ rematched: boolean; state: string }>(`${authorityPath(authority)}/rematch`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ kind: authority.kind, expectedRevision: authority.revision }),
    }),
};

export const bulkTrackRematch = {
  preview: () => json<TrackRematchAllPreview>("/api/admin/track-matches/rematch-all/preview"),
  apply: (confirmationId: string) =>
    json<{ jobId: string; created: boolean; algorithmVersion: string; trackCount: number }>(
      "/api/admin/track-matches/rematch-all/apply",
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ confirmationId }),
      },
    ),
};
