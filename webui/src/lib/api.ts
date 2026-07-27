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

export type RuntimeStatus = {
  version: string;
  backendType: string;
  durableStorage?: {
    provider: string;
    readiness: string;
    errorCode?: string | null;
    checkedAt?: string;
  };
};

export type PlaylistSummary = {
  id: string;
  name: string;
  trackCount: number;
  localTracks: number;
  externalTracks: number;
  unmatchedTracks: number;
  artworkUrl?: string | null;
  sourceProvider?: string;
};

export type PlaylistResponse = {
  playlists: PlaylistSummary[];
  inventory: {
    managed: number;
    unmanaged: number;
  };
};

export type Job = {
  id: string;
  type: string;
  state: string;
  updatedAt: string;
};

export type ActivityItem = {
  id: string;
  source: string;
  label: string;
  state: string;
  detail: string;
  occurredAt: string;
};

export type ProviderSummary = {
  providerId: string;
  connectedAccountName?: string | null;
  enabledAccountCount: number;
  capabilityTotal: number;
  healthyCapabilityCount: number;
  failedCapabilityCount: number;
  lastCheckedAt?: string | null;
};

export type ProviderDefinition = {
  id: string;
  name: string;
  logoUrl?: string | null;
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

export const home = {
  schema: () => json<{ providers: ProviderDefinition[] }>("/api/admin/ui/schema"),
  status: () => json<RuntimeStatus>("/api/admin/status"),
  playlists: () => json<PlaylistResponse>("/api/admin/playlists"),
  jobs: () => json<{ jobs: Job[] }>("/api/admin/jobs?limit=100"),
  activity: () => json<{ items: ActivityItem[] }>("/api/admin/ui/activity?limit=8"),
  providers: () => json<{ providers: ProviderSummary[] }>("/api/admin/ui/provider-summaries"),
};
