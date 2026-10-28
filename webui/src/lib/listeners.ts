import type { ListenerPlaylistCopy, PlaylistLink } from "./api";

export function originalCopyName(
  copy: Pick<ListenerPlaylistCopy, "originalPlaylistId" | "originalTargetPlaylistId" | "backendInstanceId">,
  links: Array<Pick<PlaylistLink, "id" | "name" | "targetBackendInstanceId" | "targetPlaylistId">>,
) {
  const link = links.find(item => item.targetBackendInstanceId === copy.backendInstanceId && (
    `allstarr-vpl-${item.id.replaceAll("-", "")}`.toLowerCase() === copy.originalPlaylistId.toLowerCase() ||
    Boolean(item.targetPlaylistId && (item.targetPlaylistId === copy.originalTargetPlaylistId || item.targetPlaylistId === copy.originalPlaylistId))
  ));
  return link?.name || "Original name unavailable";
}
