export function splitLyricSong(text: string): { title: string; artist?: string } {
  const value = text.trim();
  const match = /\s+([-—])\s+/.exec(value);
  if (!match) return { title: value };
  const left = value.slice(0, match.index).trim();
  const right = value.slice(match.index + match[0].length).trim();
  if (!left || !right) return { title: value };
  return match[1] === "—" ? { title: left, artist: right } : { title: right, artist: left };
}
