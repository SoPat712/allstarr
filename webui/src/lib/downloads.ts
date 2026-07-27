import type { ManagedDownload } from "./api";

export type DownloadSort = "track" | "provider" | "quality" | "size" | "updated";

export function filterDownloads(
  files: ManagedDownload[],
  query: string,
  provider: string,
  sort: DownloadSort,
) {
  const term = query.trim().toLowerCase();
  return files
    .filter((file) =>
      (!provider || file.provider === provider) &&
      (!term || [file.title, file.artist, file.album, file.fileName, file.provider]
        .some((value) => value?.toLowerCase().includes(term))))
    .toSorted((left, right) => {
      if (sort === "provider")
        return (left.provider || "").localeCompare(right.provider || "") ||
          left.title.localeCompare(right.title);
      if (sort === "quality")
        return (right.bitrateKbps ?? right.sampleRateHz ?? right.bitDepth ?? 0) -
          (left.bitrateKbps ?? left.sampleRateHz ?? left.bitDepth ?? 0);
      if (sort === "size") return right.size - left.size;
      if (sort === "updated")
        return Date.parse(right.lastModified) - Date.parse(left.lastModified);
      return `${left.artist} ${left.title}`.localeCompare(`${right.artist} ${right.title}`);
    });
}

export function qualityDetails(file: ManagedDownload) {
  return [
    file.codec,
    file.bitrateKbps ? `${file.bitrateKbps} kbps` : "",
    file.bitDepth ? `${file.bitDepth}-bit` : "",
    file.sampleRateHz ? `${formatSampleRate(file.sampleRateHz)} kHz` : "",
    file.channels ? `${file.channels} ch` : "",
  ].filter(Boolean);
}

function formatSampleRate(value: number) {
  const kilohertz = value / 1_000;
  return kilohertz.toFixed(value % 1_000 ? 1 : 0);
}
