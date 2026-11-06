import { json } from "./api-client";

export type PromotionSource = { provider: string; codec?: string | null; bitDepth?: number | null; sampleRate?: number | null; verdict?: string; reason?: string };
export type QualityCheck = { claimedSampleRate: number; claimedBitDepth: number; effectiveBandwidthHz: number | null; verdict: string; confidence: number; likelySourceRate?: number | null; energyByFrequency?: (number | null)[] | null };
export type PromotionStep = { name: string; outcome: string; reason: string };
export type PromotionProfileStep = { key: string; title: string; description: string; enabled: boolean };
export type PromotionTrack = {
  recordingId: string;
  song: { id?: string; title: string; artist: string; album: string; year?: number | null; track?: number | null; coverArtUrl?: string | null; codec?: string | null; bitDepth?: number | null; sampleRate?: number | null };
  step: string; outcome: string; reason: string; provider?: string | null;
  steps: PromotionStep[]; sourceAttempts?: { provider: string | null; outcome: string; reason: string }[];
  qualityCheck?: QualityCheck | null; destinationPath?: string | null; nextSource?: PromotionSource | null;
  lossy?: boolean;
  approvedByUserId?: string | null; approvalReason?: string | null;
};
export type PromotionRun = {
  id: string; title: string; artist: string; album?: { title: string; artist: string; year?: number | null; songCount: number; coverReference?: string | null } | null; coverReference?: string | null; unit: "album" | "song";
  counts: { total: number; added: number; needsLook: number };
  state: string; startedAt?: string | null; finishedAt?: string | null; requester?: unknown;
  step: string; outcome: string; reason?: string; tracks: PromotionTrack[];
  profileSteps?: PromotionProfileStep[];
};
export type PromotionList = { items: PromotionRun[]; nextCursor: string | null; counts: { active: number; needsLook: number; added: number; all: number; addedToday: number } };
export type PromotionRequest = { recordingId: string; backendLibraryId: string; rootId: string; requestKey: string; unit: "album" | "song" };
export type LibraryRequest = {
  id: string; requesterUserId: string; recordingId: string; releaseIdentity?: string | null;
  title: string; artist: string; year?: number | null; songCount: number; coverReference?: string | null;
  unit: "album" | "song"; state: string; runId?: string | null; createdAt: string;
  requester?: { id: string; displayName: string }; decidedAt?: string | null; declineNote?: string | null;
};
export type PromotionStart = PromotionRun | { requested: true; request: LibraryRequest };
export type PromotionPreview = {
  album: { title: string; artist: string; year?: number | null; songCount: number; coverReference?: string | null };
  canAddDirectly?: boolean; backendLibraryId?: string; rootId?: string;
  alreadyInLibrary: number; destinationPath: string; bestCopies: PromotionSource[];
  profileSteps: PromotionProfileStep[];
};
export type KeptPromotionContext = { items: { path: string; recordingId: string; backendLibraryId: string | null; inLibrary: boolean }[]; backendLibraryId: string | null; rootId: string | null; alreadyInLibrary: number };
const base = "/api/admin/library-promotions";
const post = (body?: unknown): RequestInit => ({ method: "POST", headers: { "Content-Type": "application/json" }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
export const libraryPromotions = {
  kept: () => json<KeptPromotionContext>(`${base}/kept`),
  preview: (request: PromotionRequest, signal?: AbortSignal) => json<PromotionPreview>(`${base}/preview`, { ...post(request), signal }),
  start: (request: PromotionRequest) => json<PromotionStart>(base, post(request)),
  requests: (state = "pending", signal?: AbortSignal) => json<LibraryRequest[]>(`${base}/requests?${new URLSearchParams({ state })}`, { signal }),
  createRequest: (request: PromotionRequest) => json<PromotionStart>(`${base}/requests`, post(request)),
  getRequest: (id: string, signal?: AbortSignal) => json<LibraryRequest>(`${base}/requests/${encodeURIComponent(id)}`, { signal }),
  requestPreview: (id: string, signal?: AbortSignal) => json<PromotionPreview>(`${base}/requests/${encodeURIComponent(id)}/preview`, { signal }),
  approveRequest: (id: string) => json<LibraryRequest>(`${base}/requests/${encodeURIComponent(id)}/approve`, post()),
  declineRequest: (id: string, note?: string) => json<LibraryRequest>(`${base}/requests/${encodeURIComponent(id)}/decline`, post({ note })),
  cancelRequest: (id: string) => json<LibraryRequest>(`${base}/requests/${encodeURIComponent(id)}/cancel`, post()),
  list: (state = "active", cursor?: string) => json<PromotionList>(`${base}?${new URLSearchParams({ state, limit: "100", ...(cursor ? { cursor } : {}) })}`),
  get: (id: string, signal?: AbortSignal) => json<PromotionRun>(`${base}/${encodeURIComponent(id)}`, { signal }),
  cancel: (id: string) => json<PromotionRun>(`${base}/${encodeURIComponent(id)}/cancel`, post()),
  review: (id: string, recordingId: string, action: "try-next-source" | "add-anyway" | "skip", reason?: string) => json<PromotionRun>(`${base}/${encodeURIComponent(id)}/songs/${encodeURIComponent(recordingId)}/review`, post({ action, reason })),
};
