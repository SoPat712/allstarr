import { formatCount } from "./intelligence";

export function countNoun(count: number, singular: string, plural = `${singular}s`) {
  return count === 1 ? singular : plural;
}

export function countLabel(count: number, singular: string, plural = `${singular}s`) {
  return `${formatCount(count)} ${countNoun(count, singular, plural)}`;
}
