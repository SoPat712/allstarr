type Contributor = { role: string; name: string; url: string | null };

function object(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

function contributor(value: unknown, role: string): Contributor[] {
  const item = object(value);
  if (!item || typeof item.username !== "string") return [];
  const name = item.username.trim();
  if (!name || name.length > 200 || /[\u0000-\u001f\u007f-\u009f]/.test(name)) return [];
  let url: string | null = null;
  if (typeof item.url === "string" && item.url.length <= 512 && /^https:\/\/spicylyrics\.org(?::443)?\/uid\/[A-Za-z0-9_-]+\/?$/.test(item.url)) {
    url = item.url;
  }
  return [{ role, name, url }];
}

export function lyricsAttribution(sourceId: string | null | undefined, value: unknown) {
  const attribution = object(value);
  if (sourceId !== "spicylyrics" || attribution?.service !== "spicylyrics") return null;
  const catalog = attribution.catalog === "spicy_lyrics" ? "Spicy Lyrics"
    : attribution.catalog === "apple_music" ? "Apple Music"
    : attribution.catalog === "spotify" ? "Spotify" : "Unknown source";
  return {
    catalog,
    contributors: attribution.catalog === "spicy_lyrics"
      ? [...contributor(attribution.uploader, "Uploaded by"), ...contributor(attribution.maker, "Synced by")] : [],
  };
}
