import { describe, expect, it } from "vitest";
import {
  activityAction,
  activityTone,
  activityDuration,
  activityHeadline,
  activityLink,
  activityRecovery,
  jobState,
  jobName,
  isRoutineActivity,
  jobResultLabel,
  jobSummary,
  clockRange,
  taskCadence,
  taskLastResult,
  groupAttempts,
  playArtwork,
  dayLabel,
  filterActivity,
  groupActivity,
  groupOutcome,
  humanize,
  mergeActivity,
  relativeTime,
} from "./activity";
import type { ActivityItem } from "./api";

const item = (id: string, detail: string): ActivityItem => ({
  id,
  kind: "matching",
  source: "future-extension",
  label: "Track matched",
  state: "accepted",
  detail,
  occurredAt: "2026-07-27T00:00:00Z",
  correlationId: "job-1",
  action: "track-match.evaluate",
});

describe("event log presentation", () => {
  it("groups consecutive operation events and removes retry noise", () => {
    const grouped = groupActivity([
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
    ]);
    expect(grouped).toMatchObject([{
      title: "Matched 2 songs in 2 playlists",
      entries: [{ id: "1" }, { id: "3" }],
    }]);
    expect(groupActivity([item("0", "New Song"), ...[
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
    ]])[0].key)
      .toBe(grouped[0].key);
    expect(groupActivity([
      { ...item("1", "Song A"), playlistName: "First" },
      { ...item("2", "Song A"), playlistName: "First" },
      { ...item("3", "Song B"), playlistName: "Second" },
      item("4", "Older Song"),
    ])[0].key).toBe(grouped[0].key);
    expect(groupOutcome([item("1", "Song A"), { ...item("2", "Song B"), state: "suggested" }]))
      .toBe("mixed");
  });

  it("retains loaded history while deduplicating live refreshes", () => {
    const current = Array.from({ length: 250 }, (_, index) =>
      ({ ...item(String(index), `Song ${index}`), occurredAt: `2026-07-26T${String(index % 24).padStart(2, "0")}:00:00Z` }));
    const merged = mergeActivity(current, [
      { ...item("249", "Updated"), occurredAt: "2026-07-27T00:00:00Z" },
    ]);
    expect(merged).toHaveLength(250);
    expect(merged[0]).toMatchObject({ id: "249", detail: "Updated" });
  });

  it("filters provider-neutral event fields and humanizes enums", () => {
    expect(filterActivity([item("1", "Song A")], {
      query: "future",
      category: "",
      provider: "",
      problemsOnly: false,
    })).toHaveLength(1);
    expect(filterActivity([item("1", "Song A")], {
      query: "",
      category: "",
      provider: "future-extension",
      problemsOnly: false,
    })).toHaveLength(0);
    expect(filterActivity([item("1", "Song A")], {
      query: "",
      category: "",
      provider: "",
      problemsOnly: true,
    })).toHaveLength(0);
    expect(filterActivity([{ ...item("2", "Song B"), state: "failed" }], {
      query: "",
      category: "matching",
      provider: "",
      problemsOnly: true,
    })).toHaveLength(1);
    expect(dayLabel("2026-10-12T10:00:00", new Date("2026-10-12T20:00:00"))).toBe("Today");
    expect(dayLabel("2026-10-11T10:00:00", new Date("2026-10-12T20:00:00"))).toBe("Yesterday");
    expect(activityAction({ ...item("5", "Song A"), sourceTitle: "Song A" }, () => "Lumen")?.label)
      .toBe("Review match");
    expect(humanize("provider_health.failed")).toBe("Provider health failed");
    expect(humanize("reviewrequired")).toBe("Review required");
    expect(relativeTime(null, "Never")).toBe("Never");
    expect(activityLink({ ...item("1", "Song A"), sourceTitle: "Song A" }))
      .toBe("#/library/matches?search=Song%20A");
    expect(activityLink({ ...item("2", "Cached"), kind: "caching" }))
      .toBe("#/library/cached");
    expect(activityLink({
      ...item("3", "Provider unhealthy"),
      kind: "provider_health",
      providerId: "lumen-audio",
    })).toBe("#/sources/services/lumen-audio");
    expect(activityLink({
      ...item("4", "Local library"),
      kind: "library",
      providerId: "library",
    })).toBeNull();
    expect(groupActivity([{ ...item("3", "Failed"), label: "scrobbling check" }])[0].title)
      .toBe("Scrobbling check");
  });
});

describe("background work presentation", () => {
  it("names known enrichment, import and maintenance work in older history", async () => {
    const { jobName } = await import("./activity");
    for (const [type, label] of Object.entries({
      "recording.tags.refresh": "Song details updated", "listening.musicbrainz.enrich": "Identify listened song",
      "listening-history.import": "History import", "favorite.link-union": "Merge linked favorites",
      "downloads.quality-check": "Audio quality check", "extension.session.verify": "Verify source sign-in",
      "problem-alert.delivery": "Send problem alert", "recommendation.generate": "Generate recommendations",
      "playlist.provider-source-update": "Update source playlist", "storage.verify-files": "Verify stored files",
      "configuration.legacy-media": "Import existing media", "releases.poll": "New releases check",
      "library.refresh": "Library refresh", "library.promote": "Promote song to library",
      "playback.signal.process": "Record a listen", "spotify-likes.import": "Import Spotify likes",
    })) {
      expect(jobName(type)).toBe(label);
      expect(activityHeadline({ ...item(type, "Completed"), kind: "job", label: type })).toBe(label);
    }
  });

  it("uses ordinary job names and distinguishes deferred work", async () => {
    const { jobName, jobState, progressPercent } = await import("./activity");
    expect(jobName("playlist.materialize")).toBe("Update playlist");
    expect(jobState("RetryScheduled")).toBe("Retry scheduled");
    expect(jobState("Deferred")).toBe("Waiting");
    expect(progressPercent(5, 10)).toBe(50);
    expect(progressPercent(20, 10)).toBe(100);
    expect(progressPercent(-1, 10)).toBe(0);
    expect(progressPercent(5, 0)).toBeNull();
    expect(progressPercent(NaN, 10)).toBeNull();
  });
});

describe("consecutive activity consolidation", () => {
  const songUpdate = (id: string, state = "succeeded", occurredAt = "2026-10-29T05:54:00Z"): ActivityItem => ({
    id, jobId: id, kind: "job", source: "Recordings", label: "Song details updated", state,
    detail: "Song details updated: attempt 1 completed.", action: "recording.tags.refresh",
    actorUserId: "one", occurredAt,
  });

  it("collapses one hundred identical job rows and retains every item", () => {
    const entries = Array.from({ length: 100 }, (_, index) => songUpdate(String(index)));
    const groups = groupActivity(entries);
    expect(groups).toHaveLength(1);
    expect(groups[0].title).toBe("Song details updated · 100 songs");
    expect(groups[0].entries).toEqual(entries);
    expect(groupOutcome(groups[0].entries)).toBe("succeeded");
  });

  it("keeps outcomes and intervening work separate", () => {
    const groups = groupActivity([
      songUpdate("one"), songUpdate("two", "failed"), songUpdate("three", "failed"),
      songUpdate("four"), { ...songUpdate("five"), label: "Keep a song" }, songUpdate("six"),
    ]);
    expect(groups.map(group => group.entries.length)).toEqual([1, 2, 1, 1, 1]);
    expect(groups.map(group => groupOutcome(group.entries))).toEqual(["succeeded", "failed", "succeeded", "succeeded", "succeeded"]);
  });

  it("bounds the whole group to ten minutes and leaves invalid times separate", () => {
    const groups = groupActivity([
      songUpdate("one"), songUpdate("two", "succeeded", "2026-10-29T05:44:00Z"),
      songUpdate("three", "succeeded", "2026-10-29T05:43:59Z"), songUpdate("four", "succeeded", "invalid"),
    ]);
    expect(groups.map(group => group.entries.length)).toEqual([2, 1, 1]);
  });

  it("keeps owners, sources, kinds and severity separate", () => {
    for (const changed of [
      { actorUserId: "two" }, { source: "Listening" }, { providerId: "other" },
      { kind: "caching" }, { severity: "warning" }, { action: "recording.keep" },
    ]) expect(groupActivity([songUpdate("one"), { ...songUpdate("two"), ...changed }])).toHaveLength(2);
  });

  it("counts jobs while keeping each job's earlier attempts available", () => {
    const groups = groupActivity([
      songUpdate("one"), songUpdate("two"),
      { ...songUpdate("one", "failed"), id: "earlier-one", occurredAt: "2026-10-29T05:53:00Z" },
      { ...songUpdate("two", "failed"), id: "earlier-two", occurredAt: "2026-10-29T05:53:00Z" },
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0].title).toBe("Song details updated · 2 songs");
    expect(groups[0].entries).toHaveLength(4);
    expect(groupOutcome(groups[0].entries)).toBe("succeeded");
  });

  it("consolidates ordinary events without a job or correlation", () => {
    const groups = groupActivity(["one", "two"].map(id => ({
      ...songUpdate(id), kind: "caching", jobId: null, label: "Song cached", detail: `Song ${id}`,
    })));
    expect(groups).toHaveLength(1);
    expect(groups[0].title).toBe("Song cached · 2 songs");
    expect(groups[0].entries.map(entry => entry.detail)).toEqual(["Song one", "Song two"]);
  });
});

it("keeps different household owners separate even with colliding correlations", () => {
  const first = { ...item("one", "Song A"), actorUserId: "first", actorDisplayName: "Josh" };
  const second = { ...item("two", "Song A"), actorUserId: "second", actorDisplayName: "Alex" };
  expect(groupActivity([first, second])).toHaveLength(2);
  expect(filterActivity([first, second], { query: "Alex", category: "", provider: "", problemsOnly: false })).toEqual([second]);
});

it("keeps job stages together using the current outcome and exact owner", () => {
  const done = { ...item("done", "Song kept"), kind: "job", jobId: "job", actorUserId: "one", state: "succeeded", occurredAt: "2026-10-26T12:00:09Z" };
  const failed = { ...done, id: "failed", state: "failed", detail: "Earlier source failed", occurredAt: "2026-10-26T12:00:00Z" };
  const groups = groupActivity([done, failed, { ...done, id: "other", actorUserId: "two" }]);
  expect(groups).toHaveLength(2); expect(groups[0].entries).toHaveLength(2); expect(groupOutcome(groups[0].entries)).toBe("succeeded");
  expect(filterActivity([done, failed], { query: "", category: "", provider: "", problemsOnly: true })).toEqual([]);
});
it("links known work to its own history and rejects external actions", () => {
  const jobId = "11111111-1111-4111-8111-111111111111";
  expect(activityAction({ ...item("work", "Song kept"), kind: "job", jobId }, () => "Source")).toEqual({ href: `#/activity?view=jobs&job=${jobId}`, label: "Open related work" });
  expect(activityAction({ ...item("work", "Song kept"), kind: "job", actionHref: "javascript:alert(1)", actionLabel: "Unsafe" }, () => "Source")).toBeNull();
});
it("keeps invalid dates readable instead of crashing the feed", () => {
  expect(relativeTime("not-a-date", "Time unavailable")).toBe("Time unavailable");
});

it("uses meaningful outcome tones without marking cancelled or unknown work as failed", () => {
  for (const state of ["passed", "verified", "placed", "succeeded", "ok"]) expect(activityTone(state)).toBe("success");
  for (const state of ["running", "started", "pending", "scheduled"]) expect(activityTone(state)).toBe("accent");
  for (const state of ["retrying", "Deferred", "partial", "PartiallySucceeded", "Conflicted"]) expect(activityTone(state)).toBe("warning");
  for (const state of ["failed", "rejected", "unhealthy"]) expect(activityTone(state)).toBe("danger");
  for (const state of ["cancelled", "not_checked", "unrecognized"]) expect(activityTone(state)).toBe("neutral");
  expect(activityHeadline({ ...item("old", "Unknown"), kind: "job", label: "future.opaque" })).toBe("Background task");
  expect(activityHeadline({ ...item("song", "Song"), label: "Tate McRae song cached" })).toBe("Tate McRae song cached");
  expect(activityDuration(null)).toBe(""); expect(activityDuration(NaN)).toBe(""); expect(activityDuration(-1)).toBe("");
  expect(activityDuration(10)).toBe("less than a second");
  expect(activityDuration(1000)).toBe("1 second");
  expect(activityDuration(2000)).toBe("2 seconds");
});

describe("Activity outcome tones", () => {
  const outcomes = {
    success: ["Accepted", "Pinned", "Delivered", "Healthy", "Succeeded", "Success", "Passed", "Completed", "Verified", "Placed", "Saved", "Resolved", "Ok", "Updated", "Released", "Created", "Enriched", "Merged", "already-linked", "concurrent-existing", "Rematched", "Republished"],
    danger: ["Failed", "Rejected", "Unhealthy", "Unresolved", "Error", "Blocked", "Unauthorized"],
    accent: ["Pending", "Running", "Started", "Waiting", "Queued", "Scheduled", "Checking", "Active", "In progress"],
    warning: ["Skipped", "partly_checked", "PartiallySucceeded", "Conflicted", "Conflict", "Ambiguous", "Suggested", "Retrying", "RetryScheduled", "retry_scheduled", "Deferred", "Interrupted", "Partial", "partial-failure", "Warning", "Warn", "Degraded", "Needs attention", "Needs review", "sample-unavailable", "skipped_manual", "skipped_invalid", "skipped_notfound", "skipped_forbidden", "skipped_conflict", "skipped_revision_conflict", "retry_identity_unavailable", "retry_conflict", "retry_exception", "retry_local_library_unavailable"],
    neutral: ["Cancelled", "Superseded", "not_checked", "Not checked", "Paused", "Automatic", "Unknown", "Neutral", "Info", "Information", "Debug", "Recorded", "Mixed", ""],
  } as const;
  for (const [tone, states] of Object.entries(outcomes)) {
    it.each(states)(`maps %s to ${tone}`, state => {
      expect(activityTone(state)).toBe(tone);
      expect(activityTone(state.toUpperCase())).toBe(tone);
    });
  }
  it("uses diagnostic severity for otherwise neutral outcomes", () => {
    expect(activityTone("mixed", "ERROR")).toBe("danger");
    expect(activityTone("recorded", "Warning")).toBe("warning");
    expect(activityTone("not_checked", "error")).toBe("neutral");
    expect(activityTone("partial-failure", "error")).toBe("warning");
  });
});


describe("activity labels and retry times", () => {
  it("uses the same state labels for enum and stored state spellings", () => {
    for (const state of ["RetryScheduled", "retry_scheduled", "retryscheduled"])
      expect(jobState(state)).toBe("Retry scheduled");
    for (const state of ["PartiallySucceeded", "partially_succeeded", "partiallysucceeded"])
      expect(jobState(state)).toBe("Partly done");
  });

  it("names all remaining registered work and respects supplied names", () => {
    for (const [type, label] of Object.entries({
      "mixes.refresh": "Mix refresh", "collection.migrate": "Moving your music",
      "compilation": "Compilation", "listening-history.resolve": "Find imported songs",
      "smart-playlist.materialize": "Update smart playlist",
    })) {
      expect(jobName(type)).toBe(label);
      expect(activityHeadline({ ...item(type, "Finished"), kind: "job", label: "Background task", action: type })).toBe(label);
    }
    expect(jobName("listening-history.resolve", "Background task")).toBe("Find imported songs");
    expect(jobName("mixes.refresh", "Refresh evening mixes")).toBe("Refresh evening mixes");
    expect(activityHeadline({ ...item("job", "Finished"), kind: "job", label: "mixes.refresh", displayName: "Refresh evening mixes" })).toBe("Refresh evening mixes");
  });

  it("renders retry schedules using the viewer's local clock and date", () => {
    const retry = new Date("2026-11-03T04:00:27Z");
    const clock = new Intl.DateTimeFormat(undefined, { timeStyle: "short" }).format(retry);
    expect(activityRecovery("Retry scheduled for 2026-11-03 04:00:27 UTC.", retry)).toBe(`Retrying at ${clock}`);
    const dated = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(retry);
    expect(activityRecovery("Retry scheduled for 2026-11-03 04:00:27 UTC.", new Date("2026-11-01T12:00:00Z"))).toBe(`Retrying at ${dated}`);
    expect(activityRecovery("Review your source connection.")).toBe("Review your source connection.");
    expect(activityRecovery("Retry scheduled for an unknown time.")).toBe("Retry scheduled for an unknown time.");
  });
});

it("uses sentence case while preserving acronyms and service names", () => {
  expect(humanize("reviewRequired")).toBe("Review required");
  expect(humanize("API_URL_check")).toBe("API URL check");
  expect(humanize("ISRCCheck")).toBe("ISRC check");
  expect(humanize("APIUrlCheck")).toBe("API URL check");
  expect(humanize("OIDCSignIn")).toBe("OIDC sign in");
  expect(humanize("Deferred")).toBe("Waiting");
  expect(humanize("isrc_lrc_id_oidc")).toBe("ISRC LRC ID OIDC");
  for (const name of ["Jellyfin", "Navidrome", "Subsonic", "Apple Music", "Spotify", "Deezer", "Qobuz", "Tidal", "YouTube", "SoundCloud", "Last.fm", "ListenBrainz", "AudioMuse", "SpicyLyrics", "MusicBrainz", "LRCLib"])
    expect(humanize(`${name} connection_check`)).toBe(`${name} connection check`);
  expect(humanize("apple_music_api_check")).toBe("Apple Music API check");
  expect(humanize("youtube_playback_failed")).toBe("YouTube playback failed");
  expect(humanize(null)).toBe("Unknown");
});

it("labels waiting job states consistently in stored and enum spellings", () => {
  for (const state of ["Deferred", "deferred", "DEFERRED", "Pending"])
    expect(jobState(state)).toBe("Waiting");
});


describe("activity overview details", () => {
  it("hides waiting work and settings reads while retaining failures and useful completions", () => {
    for (const state of ["succeeded", "waiting", "deferred", "RetryScheduled"])
      expect(isRoutineActivity({ ...item("routine", "Waiting for the next scheduled retry"), kind: "job", label: "Background task", state })).toBe(true);
    expect(isRoutineActivity({ ...item("settings", "Runtime settings"), kind: "administration", label: "Runtime settings source use", state: "recorded" })).toBe(true);
    expect(isRoutineActivity({ ...item("failed", "Retry needed"), kind: "job", label: "Background task", state: "failed" })).toBe(false);
    for (const label of ["Health check passing", "Library index refreshed", "Mixes refreshed"])
      expect(isRoutineActivity({ ...item(label, "Done"), label, kind: "job", state: "succeeded" })).toBe(false);
  });

  it("pairs a listen receipt with its work without merging other listeners or songs", () => {
    const work = { ...item("work", "Evening song"), kind: "job", label: "Record a listen", action: "playback.signal", correlationId: "listen", state: "succeeded", actorUserId: "one" };
    const sent = { ...work, id: "sent", kind: "scrobble", label: "Scrobble recorded", action: "scrobble.delivered", state: "delivered", sourceTitle: "Evening song" };
    expect(groupActivity([sent, work])).toHaveLength(1);
    expect(groupActivity([sent, work])[0].entries).toHaveLength(2);
    expect(groupOutcome(groupActivity([sent, work])[0].entries)).toBe("delivered");
    expect(groupActivity([sent, { ...work, actorUserId: "two" }])).toHaveLength(2);
    expect(groupActivity([sent, { ...work, correlationId: null }])).toHaveLength(2);
  });

  it("uses passing health results, concise summaries and one time for the same minute", () => {
    expect(jobResultLabel({ type: "health.smoke", state: "Succeeded", displayState: "partly_checked" })).toBe("Passing");
    expect(jobResultLabel({ type: "health.smoke", state: "Succeeded", displayState: "not_checked" })).toBe("Not checked");
    expect(jobSummary("Completed (succeeded).")).toBe("");
    expect(jobSummary("Health check: 4 passed, 3 not checked.")).toBe("Health check: 4 passed, 3 not checked.");
    const stamp = "2026-11-04T23:19:00Z";
    expect(clockRange(stamp, "2026-11-04T23:19:20Z")).not.toContain("–");
    expect(clockRange(stamp, "2026-11-04T23:21:00Z")).toContain("–");
  });

  it("formats daily schedules locally and never presents a schedule as a last-run result", () => {
    expect(taskCadence("Daily at 04:00 · UTC")).toBe("Daily 4:00 AM");
    expect(taskCadence("Weekly on Monday at 04:00 · America/New_York")).toBe("Weekly on Monday 4:00 AM");
    expect(taskLastResult("Scheduled", "2026-11-04T04:00:00Z")).toBe("Result not recorded");
    expect(taskLastResult("Scheduled", null)).toBe("Not recorded");
    expect(taskLastResult("Succeeded", "2026-11-04T04:00:00Z")).toBe("Completed");
  });

  it("groups matching attempts into a compact timeline while keeping every receipt", () => {
    const attempts = Array.from({ length: 100 }, (_, index) => ({ attemptNumber: 100 - index, startedAt: "2026-11-04T04:00:00Z", completedAt: "2026-11-04T04:01:00Z", outcome: "deferred" }));
    const grouped = groupAttempts(attempts);
    expect(grouped).toHaveLength(1);
    expect(grouped[0]).toHaveLength(100);
    expect(groupAttempts([attempts[0], { ...attempts[1], outcome: "failed", errorMessage: "Source unavailable" }, attempts[2]])).toHaveLength(3);
  });
});


it("uses retained covers and existing artwork routes for play receipts", () => {
  const play = { request: { presented: { artwork: "available" }, itemId: "song" }, resolution: { externalSnapshotId: "snapshot" } };
  expect(playArtwork(play)).toBe("/api/admin/track-matches/snapshot/artwork");
  expect(playArtwork({ ...play, resolution: {} })).toBe("/api/admin/downloads/artwork/song");
  expect(playArtwork({ request: { presented: { artwork: "/cover.jpg" } }, resolution: {} })).toBe("/cover.jpg");
  expect(playArtwork({ request: { presented: { artwork: "available" } }, resolution: {} })).toBeNull();
});
