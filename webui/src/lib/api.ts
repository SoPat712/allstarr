export type Session = {
  authenticated: boolean;
  backend: string;
  user?: {
    id: string;
    name: string;
    isAdministrator: boolean;
    avatarUrl?: string | null;
  };
};

async function json<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await fetch(input, {
    cache: "no-store",
    credentials: "same-origin",
    ...init,
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(body?.error || `${response.status} ${response.statusText}`);
  }

  return response.json() as Promise<T>;
}

export const auth = {
  session: () => json<Session>("/api/admin/auth/me"),
  login: (username: string, password: string, rememberMe: boolean) =>
    json<Session>("/api/admin/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, rememberMe }),
    }),
  logout: () => json<{ success: boolean }>("/api/admin/auth/logout", { method: "POST" }),
};
