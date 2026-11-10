import type { OidcAccess, OidcAccessDraft } from "./api";

export function oidcDraftDirty(draft: OidcAccessDraft, saved: OidcAccess): boolean {
  return draft.enabled !== saved.enabled || draft.authority !== saved.authority ||
    draft.clientId !== saved.clientId || draft.publicUrl !== saved.publicUrl ||
    draft.displayName !== (saved.displayName || "Single sign-on") ||
    !!draft.clientSecret || draft.removeClientSecret;
}

export function validateOidcDraft(draft: OidcAccessDraft, saved: OidcAccess, basePath = "") {
  const errors: Partial<Record<"authority" | "clientId" | "publicUrl" | "displayName" | "clientSecret", string>> = {};
  if (draft.clientSecret && draft.removeClientSecret) errors.clientSecret = "Choose a new secret or remove the saved one, rather than both.";
  if (draft.clientSecret.length > 4096) errors.clientSecret = "The client secret must be 4096 characters or fewer.";
  const values = { authority: draft.authority.trim().replace(/\/+$/, ""), clientId: draft.clientId.trim(), publicUrl: draft.enabled ? draft.publicUrl.trim().replace(/\/+$/, "") : draft.publicUrl.trim(), displayName: draft.displayName.trim() };
  for (const [field, label, limit] of [["authority", "Issuer address", 2048], ["clientId", "Client ID", 500], ["publicUrl", "Public Allstarr address", 2048], ["displayName", "Sign-in button name", 500]] as const) {
    if (values[field].length > limit) errors[field] = `${label} must be ${limit} characters or fewer.`;
    if (/[\u0000-\u001f\u007f-\u009f]/.test(values[field])) errors[field] = `${label} cannot contain control characters.`;
  }
  if (draft.enabled || values.authority) errors.authority ??= addressError(values.authority, "Issuer address");
  if (draft.enabled || values.publicUrl) errors.publicUrl ??= addressError(values.publicUrl, "Public Allstarr address");
  if (draft.enabled && !errors.publicUrl && new URL(draft.publicUrl.trim()).pathname.replace(/\/+$/, "") !== basePath) {
    errors.publicUrl = basePath ? `The public address must end with ${basePath}.` : "Use the public address without an extra path.";
  }
  if (draft.enabled && !values.clientId) errors.clientId = "Enter the client ID.";
  if (draft.enabled && !values.displayName) errors.displayName = "Enter a sign-in button name.";
  const keepsSecret = saved.hasClientSecret && !draft.removeClientSecret &&
    draft.authority.trim().replace(/\/+$/, "") === saved.authority && draft.clientId.trim() === saved.clientId;
  if (draft.enabled && !draft.clientSecret && !keepsSecret) errors.clientSecret = "Enter a client secret. A saved secret only works with the same issuer and client ID.";
  return Object.fromEntries(Object.entries(errors).filter(([, error]) => error)) as typeof errors;
}

function addressError(value: string, label: string): string | undefined {
  if (!value.trim()) return `Enter the ${label[0].toLowerCase()}${label.slice(1)}.`;
  try {
    const url = new URL(value.trim());
    if (!/^https?:\/\//i.test(value.trim()) || value.includes("\\") || !url.hostname || url.username || url.password || url.href.includes("?") || url.href.includes("#")) {
      return "Use an http or https address without sign-in details, a question mark or #.";
    }
    if (url.protocol === "http:" && !privateHost(url.hostname)) return "A public address must use https. A private or local address may use http.";
  } catch { return "Enter a full address, such as https://music.example.com."; }
}

function privateHost(host: string): boolean {
  if (host.startsWith("[")) {
    const address = host.slice(1, -1);
    const first = Number.parseInt(address.split(":")[0], 16);
    return address === "::1" || (first & 0xfe00) === 0xfc00 || (first & 0xffc0) === 0xfe80;
  }
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) {
    const [first, second] = host.split(".").map(Number);
    return first === 127 || first === 10 || (first === 172 && second >= 16 && second <= 31) ||
      (first === 192 && second === 168) || (first === 169 && second === 254) ||
      (first === 100 && second >= 64 && second <= 127);
  }
  return host === "localhost" || [".local", ".lan", ".home.arpa", ".internal"].some(suffix => host.endsWith(suffix));
}

export function oidcRedirectUri(publicUrl: string): string {
  const address = publicUrl.trim().replace(/\/+$/, "");
  return address ? `${address}/api/admin/auth/oidc/callback` : "";
}
