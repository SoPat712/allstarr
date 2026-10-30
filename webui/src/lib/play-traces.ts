import { ApiError, type PlayTrace } from "./api";
import { formatDuration } from "./playlists";

export function playDuration(milliseconds?: number | null): string {
  if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return "Time not recorded";
  const seconds = Math.round(milliseconds / 100) / 10;
  if (seconds < .1) return "Less than a second";
  const minutes = Math.floor(seconds / 60), remainder = Math.round((seconds % 60) * 10) / 10;
  const words = (value: number, unit: string) => `${value} ${unit}${value === 1 ? "" : "s"}`;
  return [minutes ? words(minutes, "minute") : "", remainder ? words(remainder, "second") : ""].filter(Boolean).join(" ");
}

export function playStart(milliseconds?: number | null): string {
  if (milliseconds == null || !Number.isFinite(milliseconds) || milliseconds < 0) return "Start time not recorded";
  const seconds = Math.round(milliseconds / 100) / 10;
  return `Started in ${seconds < .1 ? "less than 0.1" : seconds} s`;
}

export function playPhase(phase: string): string {
  return ({ route: "Choosing where to play", account: "Choosing an account", lease: "Waiting for a turn", source: "Opening the source",
    verify: "Checking the audio", retry: "Trying again", segments: "Loading audio pieces", process: "Preparing the audio",
    headers: "Ready to send audio", firstbyte: "Sending the first audio", preparation: "Getting the song ready" } as Record<string, string>)[phase.toLowerCase()] ?? "Preparing playback";
}

export function playSource(provider: string | null | undefined, cached: boolean | null | undefined, providers: { id: string; name: string }[]): string {
  if (cached) return "Saved copy";
  const key = provider?.toLowerCase() ?? "";
  return providers.find(item => item.id.toLowerCase() === key)?.name ??
    ({ library: "Your library", jellyfin: "Jellyfin", subsonic: "Subsonic", applemusic: "Apple Music", "apple-musickit": "Apple Music", spotify: "Spotify", tidal: "Tidal", qobuz: "Qobuz", deezer: "Deezer", youtube: "YouTube" } as Record<string, string>)[key] ?? "Music source";
}

export function playOutcome(outcome: string): { state: string; label: string } {
  return ({ started: { state: "success", label: "Started" }, completed: { state: "success", label: "Played" },
    failed: { state: "failed", label: "Could not play" }, cancelled: { state: "cancelled", label: "Stopped" } } as Record<string, { state: string; label: string }>)[outcome.toLowerCase()] ?? { state: "unknown", label: "Not recorded" };
}

export function playComparison(play: Pick<PlayTrace, "request" | "resolution" | "received" | "differences">, providers: { id: string; name: string }[]) {
  const field = (key: string, text: string) => ({ text, different: play.differences?.includes(`${key}-mismatch`) ?? false });
  const asked = play.request.presented;
  const source = field("source", playSource(play.resolution.provider, play.resolution.cached, providers));
  const rows = [{ label: "Asked for", fields: [field("title", asked.title || "Unknown song"), field("artist", asked.artist || "Unknown artist"),
    field("album", asked.album || "Unknown album"), field("duration", formatDuration(asked.durationMilliseconds))] }];
  if (play.received) {
    const { tags, durationSeconds } = play.received;
    rows.push({ label: "Played", fields: [field("title", tags?.title || "Unknown song"), field("artist", tags?.artist || "Unknown artist"),
      field("duration", formatDuration(durationSeconds == null ? null : durationSeconds * 1000)), source] });
  } else rows[0].fields.push(source);
  rows[0].fields.forEach(field => { field.different = false; });
  return rows;
}

export function playActionError(error: unknown): string {
  const message = error instanceof ApiError && error.details && typeof error.details === "object"
    ? (error.details as { error?: unknown }).error : null;
  return typeof message === "string" && message.length <= 300 && /^[\p{L}][\p{L}\p{N} ,.'’?!():\-]*[\p{L}\p{N}.!?)]$/u.test(message) && /\s/.test(message)
    ? message : "That didn't work. Try again.";
}
