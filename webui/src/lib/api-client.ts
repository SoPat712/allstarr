import { adminUrl } from "./admin-url";

export async function request(input: RequestInfo | URL, init?: RequestInit) {
  const requestInput = typeof input === "string" ? adminUrl(input) : input;
  const response = await fetch(requestInput, {
    cache: "no-store",
    credentials: "same-origin",
    ...init,
  });

  if (!response.ok) {
    const body = normalizeResponse(await response.json().catch(() => null));
    throw new ApiError(response.status, response.statusText, body);
  }
  return response;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    statusText: string,
    readonly details: unknown,
  ) {
    const body = details as { error?: string; message?: string; stage?: string } | null;
    const message = body?.error || body?.message || `${status} ${statusText}`;
    // Short failure codes need their stage for context; full sentences stand on their own.
    super(body?.stage && !/\s/.test(message) ? `${body.stage}: ${message}` : message);
  }
}

export async function json<T>(input: RequestInfo | URL, init?: RequestInit): Promise<T> {
  const response = await request(input, init);
  if (response.status === 204) return undefined as T;
  return normalizeResponse(await response.json()) as T;
}

export function normalizeResponse(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(normalizeResponse);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [
    key[0].toLowerCase() + key.slice(1),
    normalizeResponse(item),
  ]));
}
