export function oidcRedirectUri(publicUrl: string): string {
  const address = publicUrl.trim().replace(/\/+$/, "");
  return address ? `${address}/api/admin/auth/oidc/callback` : "";
}
