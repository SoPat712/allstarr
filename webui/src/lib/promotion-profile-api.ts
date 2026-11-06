import { json } from "./api-client";

export type PromotionProfile = {
  namingScript: string;
  libraryRootId: string | null;
  qualityFloor: { codecFamily: "lossless" | "lossy" | "any"; minBitDepth: number; minSampleRate: number };
  hiResCeiling: { maxBitDepth: number; maxSampleRate: number };
  fakeLosslessCheck: boolean;
  allowLossy: boolean;
  musicBrainzTags: boolean;
  coverMode: "none" | "embedded" | "cover.jpg" | "both";
  lyricsMode: "off" | ".lrc" | "embedded" | "both";
  replayGain: boolean;
  defaultUnit: "album" | "song";
  afterAdding: boolean;
  removeKeptCopy: boolean;
};
export type ApprovalRoles = "admins" | "admins-and-trusted-listeners";
export type HouseholdPromotionProfile = { values: PromotionProfile; addWithoutApproval: ApprovalRoles };
export type PersonalPromotionProfile = { values: PromotionProfile; householdDefault: HouseholdPromotionProfile; usesHouseholdDefault: boolean };
export type NamingError = { position: number; length: number; message: string };
export type NamingPreview = {
  script: string;
  scriptOnly: boolean;
  tokens: { kind: number | string; value: string }[];
  examples: { libraryTrackId: string | null; recordingId: string | null; title: string; kind: string; relativePath: string | null; errors: NamingError[] }[];
  errors: NamingError[];
};
export type PromotionRoot = { id: string; name: string };
const base = "/api/admin/library-promotions/profiles";
const put = (body: unknown): RequestInit => ({ method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
export const promotionProfiles = {
  mine: () => json<PersonalPromotionProfile>(`${base}/me`),
  household: () => json<HouseholdPromotionProfile>(`${base}/household`),
  saveMine: (profile: PromotionProfile) => json<PersonalPromotionProfile>(`${base}/me`, put(profile)),
  saveHousehold: (profile: HouseholdPromotionProfile) => json<HouseholdPromotionProfile>(`${base}/household`, put(profile)),
  preview: (script: string, signal?: AbortSignal) => json<NamingPreview>(`${base}/preview`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ script }), signal }),
  roots: () => json<PromotionRoot[]>("/api/admin/library-promotions/roots"),
};
