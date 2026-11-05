type ImageStatus = Pick<HTMLImageElement, "complete" | "naturalWidth" | "getAttribute">;
export type ImageLoadState = { key: string; index: number; loaded: boolean; failed: boolean };

export function imageHasLoaded(image: ImageStatus | undefined, source: string | undefined): boolean {
  return Boolean(source && image?.getAttribute("src") === source && image.complete && image.naturalWidth > 0);
}

export function reconcileImageSources(previous: ImageLoadState | undefined, sources: readonly string[], image?: ImageStatus): ImageLoadState {
  const key = JSON.stringify(sources);
  return previous?.key === key ? previous : { key, index: 0, loaded: imageHasLoaded(image, sources[0]), failed: false };
}
