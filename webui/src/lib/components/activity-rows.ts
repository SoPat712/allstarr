import type { PlayTrace } from "$lib/api";

export function groupPlays(plays: PlayTrace[]) {
  const groups: PlayTrace[][] = [];
  for (const play of plays) {
    const last = groups.at(-1);
    const previous = last?.[0];
    if (previous && previous.listenerId === play.listenerId &&
        previous.request.presented.title && previous.request.presented.title === play.request.presented.title &&
        previous.request.presented.artist === play.request.presented.artist && previous.request.presented.album === play.request.presented.album &&
        previous.resolution.externalSnapshotId === play.resolution.externalSnapshotId &&
        new Date(previous.startedAt).toDateString() === new Date(play.startedAt).toDateString()) last!.push(play);
    else groups.push([play]);
  }
  return groups;
}

export function plainTaskName(name: string, kind: string) {
  if (kind !== "playlist" && kind !== "playlist-sync") return name;
  return name.replace(/^Sync\s+/i, "").replace(/\s+for\s+[^,]+(?=,\s*week of\b)/i, "").replace(/,?\s+week of\s+.+$/i, "").trim();
}

export function scheduleParts(cron: string, enabled: boolean) {
  const [minute, hour, date, month, day] = cron.trim().split(/\s+/);
  const simple = /^\d+$/.test(minute) && Number(minute) < 60 && /^\d+$/.test(hour) && Number(hour) < 24 && date === "*" && month === "*";
  return {
    repeat: !enabled ? "manual" : simple && day === "*" ? "daily" : simple && /^[0-6]$/.test(day) ? "weekly" : "custom",
    time: simple ? `${hour.padStart(2, "0")}:${minute.padStart(2, "0")}` : "04:00",
    day: /^[0-6]$/.test(day) ? day : "1",
  };
}
