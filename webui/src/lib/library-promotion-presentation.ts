import type { LibraryRequest, PromotionRun, PromotionSource, PromotionTrack, QualityCheck } from "./library-promotion-api";
import type { ProviderDefinition } from "./api";
import { providerDisplayName } from "./sources";
import { humanize } from "./activity";

type Tone = "ok" | "info" | "warn" | "neutral" | "stop";
const sourceNames: Record<string, string> = { qobuz: "Qobuz", deezer: "Deezer", tidal: "Tidal", apple: "Apple Music", "apple-musickit": "Apple Music", "apple-music": "Apple Music", amazon: "Amazon Music" };
export const sourceName = (id: string, providers: ProviderDefinition[] = []) => providerDisplayName(providers, id, sourceNames[id.toLowerCase()] ?? humanize(id));
export const frequency = (hz: number) => Number((hz / 1000).toFixed(1)).toString();
export function sourceQuality(source: { codec?: string | null; bitDepth?: number | null; sampleRate?: number | null }) {
  return [source.codec?.toUpperCase(), source.bitDepth && source.sampleRate ? `${source.bitDepth}/${source.sampleRate === 44100 ? "44" : frequency(source.sampleRate)}` : source.sampleRate ? `${frequency(source.sampleRate)} kHz` : null].filter(Boolean).join(" ");
}
export function sourceLabel(source: PromotionSource, providers: ProviderDefinition[] = [], detailed = false) {
  const quality = detailed && source.codec && source.bitDepth && source.sampleRate ? `${source.codec.toUpperCase()} ${source.bitDepth}-bit / ${frequency(source.sampleRate)} kHz` : sourceQuality(source);
  return `${sourceName(source.provider, providers)}${quality ? ` · ${quality}` : ""}`;
}
export function keptQuality(file: { codec: string; quality?: string; bitDepth?: number | null; sampleRateHz?: number | null; bitrateKbps?: number | null }) {
  return /^(flac|alac|wavpack|ape|pcm_)/i.test(file.codec) ? sourceQuality({ codec: file.codec, bitDepth: file.bitDepth, sampleRate: file.sampleRateHz })
    : [file.codec.toUpperCase(), file.bitrateKbps].filter(Boolean).join(" ") || file.quality || "";
}
export const promotionArtwork = (reference?: string | null) => reference?.startsWith("ext-") ? `/api/admin/downloads/artwork/${encodeURIComponent(reference)}` : reference;
export function promotionGroup(run: PromotionRun): { id: string; label: string; chip: string; tone: Tone } {
  if (run.state === "needs-look" || run.counts.needsLook > 0) return { id: "needs-look", label: "Needs a look", chip: "Check", tone: "warn" };
  if (run.state === "added") return { id: "added", label: "Added today", chip: "In library", tone: "ok" };
  if (run.outcome === "Cancelled") return { id: "cancelled", label: "Cancelled", chip: "Cancelled", tone: "neutral" };
  if (run.state === "skipped" || run.state === "finished") return { id: "finished", label: "Finished", chip: "Skipped", tone: "neutral" };
  return { id: "active", label: "Adding now", chip: "Adding", tone: "info" };
}
export function promotionProgress(run: PromotionRun) {
  const already = run.tracks.filter(track => track.steps.some(step => step.name === "Dedupe")).length;
  const skipped = run.tracks.filter(track => track.outcome === "Skipped" && !track.steps.some(step => step.name === "Dedupe")).length;
  const total = Math.max(0, run.counts.total - already - skipped), added = Math.max(0, Math.min(total, run.counts.added - already));
  return { added, total, percent: total ? added / total * 100 : 0, label: `${added} of ${total} ${total === 1 ? "song" : "songs"}` };
}
export function songStatus(track: PromotionTrack, cancelled = false, waiting = false): { text: string; tone: Tone } {
  if (track.steps.some(step => step.name === "Dedupe") || track.steps.some(step => step.name === "Handover" && step.outcome === "Complete")) return { text: "Done", tone: "ok" };
  if (cancelled) return { text: "Cancelled", tone: "neutral" };
  if (track.outcome === "Needs review") return { text: "Needs a look", tone: "warn" };
  if (track.outcome === "Skipped" || track.outcome === "Cancelled") return { text: track.outcome, tone: "neutral" };
  if (waiting && track.outcome === "Queued") return { text: "Queued", tone: "neutral" };
  return track.outcome === "Queued" && track.steps.length === 0 ? { text: "Queued", tone: "neutral" } : { text: "Now", tone: "info" };
}
export function activePromotion(run: PromotionRun) { return run.state === "active" || run.state === "needs-look" || run.counts.needsLook > 0; }
export function canCancelPromotion(run: PromotionRun) { return activePromotion(run) && !["Placed", "Cancelled"].includes(run.outcome); }
export function qualityWording(check?: QualityCheck | null, reason?: string): { title: string; detail: string } {
  if (!check) return { title: "This copy needs a look.", detail: reason || "Review this source before adding it to your library." };
  const cutoff = check.effectiveBandwidthHz != null && Number.isFinite(check.effectiveBandwidthHz) && check.effectiveBandwidthHz > 0 ? frequency(check.effectiveBandwidthHz) : null;
  if (check.verdict === "lossy-transcode-suspected") return { title: "This copy looks upscaled from MP3.", detail: cutoff ? `Its sound stops at about ${cutoff} kHz, where a real CD-quality file keeps going to about 20 kHz.` : "The lossless check found signs of a lossy source. No reliable sound cut-off was measured." };
  if (check.verdict === "upsampled") return { title: "This copy looks upsampled.", detail: `${check.likelySourceRate ? `It appears to come from a ${frequency(check.likelySourceRate)} kHz source. ` : ""}${cutoff ? `Its sound stops at about ${cutoff} kHz, below the claimed ${frequency(check.claimedSampleRate)} kHz sample rate.` : "The lossless check could not confirm the claimed sample rate."}` };
  if (check.verdict === "padded-bit-depth") return { title: "This copy looks padded to a higher bit depth.", detail: `It claims ${check.claimedBitDepth}-bit audio, but the extra bits do not contain the detail expected at that depth.` };
  if (check.verdict === "standard" || check.verdict === "true-hi-res") return { title: "This copy passed the lossless check.", detail: reason || "Review the remaining step before adding this song." };
  return { title: "The lossless check could not confirm this copy.", detail: cutoff ? `Its measured sound cut-off is about ${cutoff} kHz. The result is uncertain; try another source or review this copy.` : "No reliable sound cut-off was measured. Try another source or review this copy." };
}

export function promotionRequestStatus(state: string): { text: string; tone: Tone } {
  return ({
    pending: { text: "Requested · waiting for an admin", tone: "neutral" },
    approved: { text: "Approved · adding to the library", tone: "info" },
    added: { text: "In library", tone: "ok" },
    declined: { text: "Request declined", tone: "neutral" },
    cancelled: { text: "Request cancelled", tone: "neutral" },
    failed: { text: "Adding needs a look", tone: "warn" },
  } satisfies Record<string, { text: string; tone: Tone }>)[state as "pending"] ?? { text: "Request status unavailable", tone: "neutral" };
}
export function requestSummary(request: Pick<LibraryRequest, "artist" | "songCount" | "requester">) {
  return `${request.artist} · requested by ${request.requester?.displayName || "a listener"} · ${request.songCount} ${request.songCount === 1 ? "song" : "songs"}`;
}
export function requestLibraryFact(already: number, total: number) {
  if (already <= 0) return total === 1 ? "Its song is not there yet" : `None of its ${total} songs`;
  if (already >= total) return total === 1 ? "Its song is already there" : `All ${total} songs`;
  return `${already} of its ${total} songs`;
}

type TimelineStep = { title: string; detail: string; tone: Tone; state: "complete" | "current" | "upcoming" | "skipped" };
export function promotionTimeline(run: PromotionRun, definitions: ProviderDefinition[] = []): TimelineStep[] {
  const groups = [ ["Acquire"], ["Quality"], ["Tags"], ["Lyrics", "ReplayGain"], ["Placement", "Refresh"], ["Handover"] ];
  return groups.flatMap<TimelineStep>(names => {
    const entries = run.tracks.flatMap(track => track.steps.filter(step => names.includes(step.name)).map(step => ({ track, step })));
    if (!entries.length) {
      const profileKey = names[0] === "Quality" ? "quality" : names[0] === "Tags" ? "tags-cover" : "";
      if (profileKey && run.profileSteps?.some(step => step.key === profileKey && !step.enabled))
        return [{ title: names[0] === "Quality" ? "Lossless check is off" : "Tags and cover art are off", detail: "Off in your profile", tone: "neutral", state: "skipped" }];
      if (names[0] === "Lyrics" && ["lyrics", "replaygain"].every(key => run.profileSteps?.some(step => step.key === key && !step.enabled)))
        return [{ title: "Lyrics and ReplayGain are off", detail: "Off in your profile", tone: "neutral", state: "skipped" }];
      const titles = ["Download the best copy", "Check the copy is really lossless", "Tag from MusicBrainz", "Synced lyrics and ReplayGain", "Place in the library folder and rescan", "Switch playlists and hearts to the library copy"];
      const index = groups.indexOf(names);
      const working = run.outcome !== "Cancelled" && run.tracks.some(track => track.outcome === "Running" && names.includes(track.step));
      return [{ title: titles[index], detail: working ? run.tracks.find(track => track.outcome === "Running" && names.includes(track.step))?.reason || "Working" : index === 5 ? "After the library finds the files" : "Next", tone: working ? "info" as const : "neutral" as const, state: working ? "current" as const : "upcoming" as const }];
    }
    const complete = entries.filter(({ step }) => step.outcome === "Complete");
    const savedStep = run.profileSteps?.find(step => step.key === (names[0] === "Quality" ? "quality" : names[0] === "Tags" ? "tags-cover" : ""));
    const skipped = savedStep?.enabled === false || entries.every(({ step }) => step.outcome === "Skipped" || /disabled|off in your profile/i.test(step.reason));
    const warn = entries.some(({ track, step }) => step.outcome === "Needs review" && !(step.name === "Quality" && track.approvedByUserId) || step.outcome === "Failed");
    const acceptedAfterReview = names[0] === "Quality" ? new Set(entries.filter(({ track, step }) => track.approvedByUserId || step.outcome === "Complete" && track.qualityCheck && !["standard", "true-hi-res"].includes(track.qualityCheck.verdict)).map(({ track }) => track.recordingId)).size : 0;
    const count = new Set(complete.map(({ track }) => track.recordingId)).size;
    const songs = `${count} ${count === 1 ? "song" : "songs"}`;
    const providers = [...new Set(complete.flatMap(({ track }) => track.provider ? [sourceName(track.provider, definitions)] : []))];
    let title = names[0] === "Acquire" ? count ? `Downloaded ${songs}${providers.length ? ` from ${providers.join(" and ")}` : ""}` : "Downloading the best copy"
      : names[0] === "Quality" ? skipped ? "Lossless check is off" : warn ? "A copy needs a lossless check review" : acceptedAfterReview ? `${acceptedAfterReview} ${acceptedAfterReview === 1 ? "song" : "songs"} accepted after review` : `${songs} passed the lossless check`
      : names[0] === "Tags" ? savedStep?.title.includes("MusicBrainz") ? "Tagged from MusicBrainz" : "Tags and cover art written"
      : names[0] === "Lyrics" ? "Synced lyrics and ReplayGain"
      : names[0] === "Placement" ? "Placed in the library folder and rescanned"
      : "Switched playlists and hearts to the library copy";
    const required = names.filter(name => !run.profileSteps?.some(step => step.key === (name === "Lyrics" ? "lyrics" : name === "ReplayGain" ? "replaygain" : "") && !step.enabled));
    const pending = entries.some(({ step }) => !["Complete", "Skipped", "None fits"].includes(step.outcome)) || run.outcome !== "Cancelled" && run.tracks.filter(track => track.outcome !== "Skipped").some(track => required.some(name => !track.steps.some(step => step.name === name && ["Complete", "Skipped", "None fits"].includes(step.outcome))));
    if (names[0] === "Lyrics" && entries.some(({ step }) => step.name === "Lyrics") && entries.filter(({ step }) => step.name === "Lyrics").every(({ step }) => step.outcome === "None fits")) title = entries.some(({ step }) => step.name === "ReplayGain") ? "ReplayGain measured; no synced lyrics fit" : "No synced lyrics fit these songs";
    if (pending && names[0] === "Tags") title = "Tagging from MusicBrainz";
    if (pending && names[0] === "Placement") title = "Place in the library folder and rescan";
    if (pending && names[0] === "Handover") title = "Switch playlists and hearts to the library copy";
    const approvalReasons = [...new Set(entries.flatMap(({ track }) => track.approvalReason ? [`Accepted: ${track.approvalReason}`] : []))];
    let detail = acceptedAfterReview && approvalReasons.length ? approvalReasons.join(" · ") : names[0] === "Quality" && !skipped && !warn && !acceptedAfterReview && complete.every(({ track }) => track.qualityCheck && ["standard", "true-hi-res"].includes(track.qualityCheck.verdict)) ? "No upscaled files found"
      : [...new Set(entries.map(({ step }) => step.reason).filter(Boolean))].join(" · ");
    if (names[0] === "Handover" && run.profileSteps?.some(step => step.key === "handover" && !step.enabled)) title = pending ? "Waiting for library confirmation" : "Confirmed the library copy";
    if (pending && !acceptedAfterReview) {
      const unfinished = entries.filter(({ step }) => !["Complete", "Skipped", "None fits"].includes(step.outcome));
      detail = [...new Set(unfinished.map(({ step }) => step.reason).filter(Boolean))].join(" · ") || "Waiting for the remaining songs";
    }
    return [{ title, detail, state: skipped ? "skipped" as const : pending ? "current" as const : "complete" as const, tone: skipped ? "neutral" as const : warn || acceptedAfterReview ? "warn" as const : pending ? "info" as const : "ok" as const }];
  });
}
