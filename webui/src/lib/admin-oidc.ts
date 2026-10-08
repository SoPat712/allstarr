import { json, type Session } from "./api";

export type OidcStatus = {
  enabled: boolean;
  displayName?: string;
  loginUrl?: string;
  linkPending?: boolean;
  linked?: boolean;
  csrfToken?: string;
};

export const adminOidc = {
  status: () => json<OidcStatus>("/api/admin/auth/oidc/status"),
  link: (username: string, password: string, rememberMe: boolean, csrfToken: string, managePlaylists = false) =>
    json<Session>("/api/admin/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Allstarr-CSRF": csrfToken },
      body: JSON.stringify({ username, password, rememberMe, linkOidc: true, managePlaylists }),
    }),
  unlink: (csrfToken: string) => json<{ success: boolean }>("/api/admin/auth/oidc/link", {
    method: "DELETE", headers: { "X-Allstarr-CSRF": csrfToken },
  }),
  cancel: (csrfToken: string) => json<{ success: boolean }>("/api/admin/auth/oidc/pending", {
    method: "DELETE", headers: { "X-Allstarr-CSRF": csrfToken },
  }),
};
