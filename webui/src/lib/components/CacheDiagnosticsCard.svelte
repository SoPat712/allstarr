<script lang="ts">
  import type { CacheDiagnostics, CacheMaintenancePreview, CacheTierUsage } from "$lib/api";
  import { humanize } from "$lib/sources";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { ChevronDown } from "@lucide/svelte";

  let {
    snapshot,
    preview,
    busy,
    onClean,
    onPurge,
  }: {
    snapshot: CacheDiagnostics | null;
    preview: CacheMaintenancePreview | null;
    busy: boolean;
    onClean: () => void;
    onPurge: (target: string) => void;
  } = $props();

  const totalBytes = $derived((snapshot?.memory.payloadBytes ?? 0) + (snapshot?.media.payloadBytes ?? 0));
  const reclaimableBytes = $derived(
    (preview?.metadata.reclaimableBytes ?? 0) +
    (preview?.media.reclaimableBytes ?? 0) +
    (preview?.unreferencedArtworkBytes ?? 0),
  );
  const misses = $derived(
    (snapshot?.memory.misses ?? 0) + (snapshot?.media.misses ?? 0),
  );
  const coalescingRatio = $derived(
    misses ? (snapshot?.activity.coalescedRequests ?? 0) / misses : 0,
  );

  function bytes(value?: number | null) {
    if (!value) return "0 B";
    const units = ["B", "KiB", "MiB", "GiB", "TiB"];
    const power = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
    return `${(value / 1024 ** power).toFixed(power ? 1 : 0)} ${units[power]}`;
  }

  function duration(seconds: number) {
    if (seconds >= 86_400) return `${Math.round(seconds / 86_400)}d`;
    if (seconds >= 3_600) return `${Math.round(seconds / 3_600)}h`;
    if (seconds >= 60) return `${Math.round(seconds / 60)}m`;
    return `${seconds}s`;
  }

  function share(used?: number | null, maximum?: number | null) {
    return maximum ? Math.min(100, ((used ?? 0) / maximum) * 100) : 0;
  }

  function hitMiss(tier?: CacheTierUsage) {
    const requests = (tier?.hits ?? 0) + (tier?.misses ?? 0);
    const hits = requests ? (tier?.hits ?? 0) / requests : 0;
    const misses = requests ? 1 - hits : 0;
    return `${(hits * 100).toFixed(0)}% / ${(misses * 100).toFixed(0)}%`;
  }
</script>

<article class="settings-card maintenance-card cache-diagnostics-card">
  <header>
    <div><strong>Cache usage</strong><small>Temporary copies of metadata, artwork and lyrics. Safe to clear at any time.</small></div>
    <span class="cache-total">{bytes(totalBytes)}</span>
  </header>

  <dl class="cache-tier-grid">
    <div>
      <dt>Memory</dt>
      <dd>{bytes(snapshot?.memory.payloadBytes)} of {bytes(snapshot?.memory.maximumBytes)}</dd>
      <span class="cache-meter" aria-hidden="true"><span style={`width: ${share(snapshot?.memory.payloadBytes, snapshot?.memory.maximumBytes)}%`}></span></span>
    </div>
    <div>
      <dt>Disk</dt>
      <dd>{bytes(snapshot?.media.payloadBytes)} of {bytes(snapshot?.media.maximumBytes)}</dd>
      <span class="cache-meter" aria-hidden="true"><span style={`width: ${share(snapshot?.media.payloadBytes, snapshot?.media.maximumBytes)}%`}></span></span>
    </div>
    <div>
      <dt>Can be cleaned now</dt>
      <dd>{bytes(reclaimableBytes)}</dd>
    </div>
  </dl>

  <dl class="cache-activity" aria-label="Cache activity">
    <div><dt>Memory hit / miss</dt><dd>{hitMiss(snapshot?.memory)}</dd></div>
    <div><dt>Disk hit / miss</dt><dd>{hitMiss(snapshot?.media)}</dd></div>
    <div><dt>Shared requests</dt><dd>{(coalescingRatio * 100).toFixed(0)}%</dd></div>
    <div><dt>Stale serves</dt><dd>{snapshot?.activity.staleServes ?? 0}</dd></div>
    <div><dt>Downloads avoided</dt><dd>{bytes(snapshot?.activity.upstreamBytesAvoided)}</dd></div>
    <div><dt>Evictions</dt><dd>{(snapshot?.memory.evictions ?? 0) + (snapshot?.media.evictions ?? 0)}</dd></div>
  </dl>

  <details class="cache-details">
    <summary><span><strong>Category budgets</strong><small>{snapshot?.categories.length ?? 0} categories with their own limits</small></span><ChevronDown size={18} aria-hidden="true" /></summary>
    <div class="cache-category-list">
      {#each snapshot?.categories ?? [] as category}
        <article>
          <span>
            <strong>{humanize(category.category)}</strong>
            <small>{category.owner} · {humanize(category.storageTier)}{category.enabled ? "" : " · Disabled"}</small>
          </span>
          <span>
            <strong>{bytes(category.payloadBytes)} of {bytes(category.maximumBytes)}</strong>
            <small>{category.entryCount} of {category.maximumEntries} entries · fresh for {duration(category.freshSeconds)}</small>
          </span>
          <Button variant="secondary" size="sm" disabled={busy || category.entryCount === 0} onclick={() => onPurge(category.category)}>
            Purge
          </Button>
        </article>
      {/each}
    </div>
  </details>

  <details class="cache-details">
    <summary><span><strong>Limits and cleanup preview</strong><small>What a cleanup would remove, before anything is deleted</small></span><ChevronDown size={18} aria-hidden="true" /></summary>
    <dl class="cache-preview-grid">
      <div><dt>Artwork entry limit</dt><dd>{bytes(snapshot?.artworkLimits.maximumEntryBytes)}</dd></div>
      <div><dt>Decoded artwork limit</dt><dd>{(snapshot?.artworkLimits.maximumDecodedPixels ?? 0).toLocaleString()} pixels</dd></div>
      <div><dt>Cleanup runs every</dt><dd>{duration(preview?.media.cleanupIntervalSeconds ?? 0)}</dd></div>
      <div><dt>Expired</dt><dd>{(preview?.metadata.expiredEntries ?? 0) + (preview?.media.expiredEntries ?? 0)}</dd></div>
      <div><dt>No expiry</dt><dd>{(preview?.metadata.noExpiryEntries ?? 0) + (preview?.media.noExpiryEntries ?? 0)}</dd></div>
      <div><dt>Stale account scopes</dt><dd>{preview?.metadata.staleAuthorizationScopeEntries ?? 0}</dd></div>
      <div><dt>Obsolete revisions</dt><dd>{preview?.metadata.supersededEntries ?? 0}</dd></div>
      <div><dt>Orphaned or malformed</dt><dd>{(preview?.metadata.unknownOwnerEntries ?? 0) + (preview?.media.orphanedMetadataFiles ?? 0) + (preview?.media.orphanedPayloadFiles ?? 0) + (preview?.media.malformedMetadataFiles ?? 0) + (preview?.unreferencedArtworkPayloads ?? 0)}</dd></div>
      <div><dt>Over quota</dt><dd>{(preview?.metadata.overQuotaEntries ?? 0) + (preview?.media.overQuotaEntries ?? 0)}</dd></div>
      <div><dt>Extension storage</dt><dd>{bytes(snapshot?.extensionStorage.payloadBytes)} of {bytes(snapshot?.extensionStorage.maximumBytes)}</dd></div>
      <div><dt>Last automatic cleanup</dt><dd>{preview?.media.lastCleanupAt ? `${new Date(preview.media.lastCleanupAt).toLocaleString()} · ${preview.media.lastCleanupDeletedEntries} removed` : "Not yet run"}</dd></div>
    </dl>
    {#if preview?.metadata.scanLimitReached || preview?.media.scanLimitReached || preview?.artworkReferenceScanLimitReached}
      <p class="cache-scan-warning">The preview stopped at its scan limit, so another cleanup may find more to remove.</p>
    {/if}
  </details>

  <footer class="maintenance-actions cache-actions">
    <Button variant="destructive" disabled={busy} onclick={() => onPurge("all")}>Purge all cache</Button>
    <span class="cache-actions-spacer"></span>
    <Button variant="secondary" disabled={busy} onclick={() => onPurge("metadata")}>Purge metadata</Button>
    <Button variant="secondary" disabled={busy} onclick={() => onPurge("media")}>Purge media</Button>
    <Button disabled={busy} onclick={onClean}>{busy ? "Working…" : "Clean reclaimable entries"}</Button>
  </footer>
</article>
