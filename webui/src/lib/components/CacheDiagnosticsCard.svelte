<script lang="ts">
  import type { CacheDiagnostics, CacheMaintenancePreview, CacheTierUsage } from "$lib/api";
  import { humanize } from "$lib/sources";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";

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

  const totalBytes = $derived((snapshot?.database.payloadBytes ?? 0) + (snapshot?.media.payloadBytes ?? 0));
  const reclaimableBytes = $derived(
    (preview?.metadata.reclaimableBytes ?? 0) +
    (preview?.media.reclaimableBytes ?? 0) +
    (preview?.unreferencedArtworkBytes ?? 0),
  );
  const misses = $derived(
    (snapshot?.database.misses ?? 0) + (snapshot?.hot.misses ?? 0) + (snapshot?.media.misses ?? 0),
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

  function hitMiss(tier?: CacheTierUsage) {
    const requests = (tier?.hits ?? 0) + (tier?.misses ?? 0);
    const hits = requests ? (tier?.hits ?? 0) / requests : 0;
    const misses = requests ? 1 - hits : 0;
    return `${(hits * 100).toFixed(0)}% / ${(misses * 100).toFixed(0)}%`;
  }
</script>

<article class="panel maintenance-card cache-diagnostics-card">
  <header>
    <div><strong>Application cache</strong><small>Disposable PostgreSQL metadata, bounded RAM, and disk media</small></div>
    <span>{bytes(totalBytes)}</span>
  </header>

  <dl class="cache-tier-grid">
    <div><dt>Metadata</dt><dd>{snapshot?.database.entryCount ?? 0} · {bytes(snapshot?.database.payloadBytes)}</dd></div>
    <div><dt>Hot RAM</dt><dd>{bytes(snapshot?.hot.payloadBytes)} / {bytes(snapshot?.hot.maximumBytes)}</dd></div>
    <div><dt>Disk media</dt><dd>{bytes(snapshot?.media.payloadBytes)} / {bytes(snapshot?.media.maximumBytes)}</dd></div>
    <div><dt>Reclaimable</dt><dd>{bytes(reclaimableBytes)}</dd></div>
  </dl>

  <div class="cache-activity" aria-label="Cache activity">
    <span><strong>{hitMiss(snapshot?.database)}</strong><small>Metadata hit / miss</small></span>
    <span><strong>{hitMiss(snapshot?.hot)}</strong><small>Hot RAM hit / miss</small></span>
    <span><strong>{hitMiss(snapshot?.media)}</strong><small>Media hit / miss</small></span>
    <span><strong>{(coalescingRatio * 100).toFixed(0)}%</strong><small>Coalesced misses</small></span>
    <span><strong>{snapshot?.activity.staleServes ?? 0}</strong><small>Stale serves</small></span>
    <span><strong>{bytes(snapshot?.activity.upstreamBytesAvoided)}</strong><small>Upstream avoided</small></span>
    <span><strong>{(snapshot?.database.evictions ?? 0) + (snapshot?.media.evictions ?? 0)}</strong><small>Evictions</small></span>
  </div>

  <details class="cache-details">
    <summary><span><strong>Category budgets</strong><small>{snapshot?.categories.length ?? 0} policy-owned categories</small></span></summary>
    <div class="cache-category-list">
      {#each snapshot?.categories ?? [] as category}
        <article>
          <span>
            <strong>{humanize(category.category)}</strong>
            <small>{category.owner} · {humanize(category.storageTier)}</small>
            <Badge state={category.enabled ? "healthy" : "suggested"}>{category.enabled ? "Enabled" : "Disabled"}</Badge>
          </span>
          <span>
            <strong>{bytes(category.payloadBytes)} / {bytes(category.maximumBytes)}</strong>
            <small>{category.entryCount} / {category.maximumEntries} entries · {duration(category.freshSeconds)} fresh</small>
          </span>
          <Button variant="destructive" size="sm" disabled={busy || category.entryCount === 0} onclick={() => onPurge(category.category)}>
            Purge
          </Button>
        </article>
      {/each}
    </div>
  </details>

  <details class="cache-details">
    <summary><span><strong>Limits and cleanup preview</strong><small>Dry-run facts before deletion</small></span></summary>
    <dl class="cache-preview-grid">
      <div><dt>Artwork entry limit</dt><dd>{bytes(snapshot?.artworkLimits.maximumEntryBytes)}</dd></div>
      <div><dt>Decoded artwork limit</dt><dd>{(snapshot?.artworkLimits.maximumDecodedPixels ?? 0).toLocaleString()} pixels</dd></div>
      <div><dt>Cleanup cadence</dt><dd>{duration(preview?.media.cleanupIntervalSeconds ?? 0)}</dd></div>
      <div><dt>Expired</dt><dd>{(preview?.metadata.expiredEntries ?? 0) + (preview?.media.expiredEntries ?? 0)}</dd></div>
      <div><dt>No TTL</dt><dd>{(preview?.metadata.noExpiryEntries ?? 0) + (preview?.media.noExpiryEntries ?? 0)}</dd></div>
      <div><dt>Stale account scopes</dt><dd>{preview?.metadata.staleAuthorizationScopeEntries ?? 0}</dd></div>
      <div><dt>Obsolete revisions</dt><dd>{preview?.metadata.supersededEntries ?? 0}</dd></div>
      <div><dt>Orphan / malformed</dt><dd>{(preview?.metadata.unknownOwnerEntries ?? 0) + (preview?.media.orphanedMetadataFiles ?? 0) + (preview?.media.orphanedPayloadFiles ?? 0) + (preview?.media.malformedMetadataFiles ?? 0) + (preview?.unreferencedArtworkPayloads ?? 0)}</dd></div>
      <div><dt>Over quota</dt><dd>{(preview?.metadata.overQuotaEntries ?? 0) + (preview?.media.overQuotaEntries ?? 0)}</dd></div>
      <div><dt>Extension storage</dt><dd>{bytes(snapshot?.extensionStorage.payloadBytes)} / {bytes(snapshot?.extensionStorage.maximumBytes)}</dd></div>
      <div><dt>Last proactive cleanup</dt><dd>{preview?.media.lastCleanupAt ? `${new Date(preview.media.lastCleanupAt).toLocaleString()} · ${preview.media.lastCleanupDeletedEntries} removed` : "Not yet run"}</dd></div>
    </dl>
    {#if preview?.metadata.scanLimitReached || preview?.media.scanLimitReached || preview?.artworkReferenceScanLimitReached}
      <p class="cache-scan-warning">The bounded preview reached its scan limit; another cleanup pass may find more disposable entries.</p>
    {/if}
  </details>

  <div class="maintenance-actions cache-actions">
    <Button disabled={busy} onclick={onClean}>{busy ? "Working…" : "Clean reclaimable entries"}</Button>
    <Button variant="secondary" disabled={busy} onclick={() => onPurge("metadata")}>Purge metadata</Button>
    <Button variant="secondary" disabled={busy} onclick={() => onPurge("media")}>Purge media</Button>
    <Button variant="destructive" disabled={busy} onclick={() => onPurge("all")}>Purge all cache</Button>
  </div>
</article>
