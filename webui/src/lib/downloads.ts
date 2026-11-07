import type { ManagedDownload } from "./api";

export function keptByText(keepers: ManagedDownload["keptBy"]) {
  if (!keepers?.length) return "";
  if (keepers.length > 1) return `${keepers[0].displayName} and ${keepers.length - 1} ${keepers.length === 2 ? "other" : "others"}`;
  const keeper = keepers[0];
  const reason = keeper.reasons.length === 1 ? { favorite: "♥ ", keep: "Keep · ", playlist: "Playlist · " }[keeper.reasons[0]] ?? "" : "";
  return `${reason}${keeper.displayName}`;
}

export function compareKeptBy(left: ManagedDownload, right: ManagedDownload) {
  const leftName = left.keptBy?.map(person => person.displayName).join(", ") ?? "", rightName = right.keptBy?.map(person => person.displayName).join(", ") ?? "";
  return Number(!leftName) - Number(!rightName) || leftName.localeCompare(rightName);
}

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

export function downloadQualityLabel(file: ManagedDownload) {
  return [file.codec, file.bitDepth && file.sampleRateHz ? `${file.bitDepth}/${file.sampleRateHz / 1000}` : file.bitrateKbps ? `${file.bitrateKbps} kbps` : file.quality].filter(Boolean).join(" ");
}

export function qualityWarning(file: ManagedDownload) {
  const check = file.qualityCheck;
  if (check?.verdict === "upsampled") return { label: "Likely upscaled", detail: `The measured bandwidth suggests a ${check.likelySourceRate === 48000 ? "48" : "44.1"} kHz source in this higher-rate file.` };
  if (check?.verdict === "padded-bit-depth") return { label: "Padded", detail: "The low sample bits suggest 16-bit audio padded to 24-bit." };
  if (check?.verdict === "lossy-transcode-suspected") return { label: "Lossy source?", detail: "A sharp high-frequency cutoff suggests a lossy source inside this lossless file." };
  return null;
}

export function formatDownloadSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  const unit = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), 4);
  return `${(bytes / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: 2 })} ${["B", "KiB", "MiB", "GiB", "TiB"][unit]}`;
}

export function lowerDownloadQuality(file: ManagedDownload) {
  return /^(mp3|aac|opus|ogg|vorbis|wma)$/i.test(file.codec) ||
    Boolean(file.sampleRateHz && file.sampleRateHz < 44100) || Boolean(file.bitDepth && file.bitDepth < 16);
}

function formatSampleRate(value: number) {
  const kilohertz = value / 1_000;
  return kilohertz.toFixed(value % 1_000 ? 1 : 0);
}


export function groupDownloadSongs(files: ManagedDownload[]) {
  const groups: { key: string; files: ManagedDownload[] }[] = [];
  const normalize = (value: string) => value.trim().toLocaleLowerCase();
  for (const file of files) {
    const group = file.title && file.artist ? groups.find(group => {
      const first = group.files[0];
      if (normalize(first.title) !== normalize(file.title) || normalize(first.artist) !== normalize(file.artist)) return false;
      if (first.durationMilliseconds && file.durationMilliseconds)
        return Math.abs(first.durationMilliseconds - file.durationMilliseconds) <= 2000;
      return Boolean(file.album && normalize(first.album) === normalize(file.album));
    }) : undefined;
    if (group) group.files.push(file);
    else groups.push({ key: file.path, files: [file] });
  }
  const quality = (file: ManagedDownload) => (/^(flac|alac|wav|aiff)$/i.test(file.codec) ? 1e12 : 0) +
    (file.bitDepth ?? 0) * 1e9 + (file.sampleRateHz ?? 0) * 1e3 + (file.bitrateKbps ?? 0);
  return groups.map(group => ({ ...group, files: group.files.toSorted((a, b) => quality(b) - quality(a) || a.path.localeCompare(b.path)) }));
}
