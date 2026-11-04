import { json } from "./api-client";
import type { BackendAccess, FileVerification, OidcAccess, OnboardingState, PlaylistConsentStatus, Session } from "./api";

export const auth = {
  session: () => json<Session>("/api/admin/auth/me"),
  login: (username: string, password: string, rememberMe: boolean, managePlaylists = false) =>
    json<Session>("/api/admin/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, rememberMe, managePlaylists }),
    }),
  playlistConsent: () =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent"),
  grantPlaylistConsent: (password: string) =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password }),
    }),
  revokePlaylistConsent: () =>
    json<PlaylistConsentStatus>("/api/admin/auth/playlist-consent", { method: "DELETE" }),
  logout: () => json<{ success: boolean }>("/api/admin/auth/logout", { method: "POST" }),
};

export const onboarding = {
  status: () => json<OnboardingState>("/api/admin/onboarding/status"),
  complete: () => json<OnboardingState>("/api/admin/onboarding/complete", { method: "POST" }),
  step: (stepId: string, action: string, expectedRevision: number) => json<OnboardingState>("/api/admin/onboarding/step", {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ stepId, action, expectedRevision }),
  }),
  reopen: () => json<OnboardingState>("/api/admin/onboarding/reopen", { method: "POST" }),
};

export const setupAccess = {
  initial: () => json<{ configured: boolean; storageReady: boolean; csrfToken: string }>("/api/admin/auth/setup"),
  connect: (value: object, csrf: string) => json<{ requiresRestart: boolean }>("/api/admin/auth/setup", accessBody(value, csrf, "POST")),
  oidc: () => json<{ settings: OidcAccess; csrfToken: string }>("/api/admin/access/oidc"),
  backend: () => json<{ settings: BackendAccess; csrfToken: string }>("/api/admin/access/backend"),
  save: (kind: "oidc" | "backend", value: object, csrf: string) => json<{ requiresRestart: boolean }>(`/api/admin/access/${kind}`, accessBody(value, csrf, "PUT")),
  files: () => json<{ settings: FileVerification; csrfToken: string }>("/api/admin/storage/files"),
  verify: (csrf: string) => json<{ message: string }>("/api/admin/storage/files/verify", accessBody({}, csrf, "POST")),
};
function accessBody(value: object, csrf: string, method: string) {
  return { method, headers: { "Content-Type": "application/json", "X-Allstarr-CSRF": csrf }, body: JSON.stringify(value) };
}
