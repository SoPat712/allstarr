import {
  json,
  type ExpectedMatchAuthority,
  type ManualMatchAuthority,
  type MatchAuthorityScope,
  type MatchReviewItem,
  type TrackRematchAllPreview,
} from "$lib/api";

export function authorityScopeOf(authority: ManualMatchAuthority): MatchAuthorityScope {
  return authority.scope ?? "personal";
}

export function allowedAuthorityScopes(match: MatchReviewItem): MatchAuthorityScope[] {
  const scopes = match.allowedAuthorityScopes?.filter((scope) =>
    scope === "personal" || scope === "household") ?? [];
  return scopes.length ? [...new Set(scopes)] : ["personal"];
}

export function authorityForScope(
  match: MatchReviewItem,
  scope: MatchAuthorityScope,
): ManualMatchAuthority | null {
  return match.manualAuthorities?.find((authority) => authorityScopeOf(authority) === scope) ?? null;
}

export function expectedAuthorityForScope(
  match: MatchReviewItem,
  scope: MatchAuthorityScope,
): ExpectedMatchAuthority {
  const authority = authorityForScope(match, scope);
  return authority ? { id: authority.id, revision: authority.revision } : null;
}

export function canEditAuthority(authority: ManualMatchAuthority): boolean {
  return authority.canEdit ?? authorityScopeOf(authority) === "personal";
}

export function canEditAuthorityScope(match: MatchReviewItem, scope: MatchAuthorityScope): boolean {
  const authority = authorityForScope(match, scope);
  return authority == null || canEditAuthority(authority);
}

const authorityPath = (authority: ManualMatchAuthority) =>
  `/api/admin/track-matches/${encodeURIComponent(authority.authoritySnapshotId)}` +
  `/manual-authorities/${encodeURIComponent(authority.id)}`;

export const manualMatchAuthority = {
  delete: (authority: ManualMatchAuthority) => {
    const query = new URLSearchParams({
      kind: authority.kind,
      expectedRevision: String(authority.revision),
      authorityScope: authorityScopeOf(authority),
    });
    return json<void>(`${authorityPath(authority)}?${query}`, { method: "DELETE" });
  },
  rematch: (authority: ManualMatchAuthority) =>
    json<{ rematched: boolean; state: string }>(`${authorityPath(authority)}/rematch`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: authority.kind,
        expectedRevision: authority.revision,
        authorityScope: authorityScopeOf(authority),
      }),
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
