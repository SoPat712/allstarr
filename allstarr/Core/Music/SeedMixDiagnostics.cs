using System.Diagnostics;

namespace allstarr.Core.Music;

internal sealed class SeedMixDiagnostics(ILogger<PersonalMixService>? logger) : IDisposable
{
    private static readonly string[] Sources = ["radio", "audiomuse", "library", "heart"];
    private static readonly string[] Stages = ["found", "without-seed", "unique", "details", "policy", "never", "playable", "skips", "filters", "evidence",
        "revalidated-never", "revalidated-policy", "access", "eligible", "artist-checked", "artist-kept", "selected"];
    private static readonly string[] Phases = ["queue", "setup", "setup-user", "setup-history", "access", "seed", "sources", "sound", "radio", "seed-provider", "provider-similar",
        "artist-tracks", "similar-tracks", "suggestion-search", "related-library", "rank", "rank-tags", "rank-taste", "rank-rules", "playback", "hearts", "fallback-library", "revalidate", "select", "save"];
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Dictionary<(string Source, string Stage), int> counts = [];
    private readonly Dictionary<string, int> discovery = [];
    private readonly Dictionary<string, long> timings = [];
    private string lastFm = "lastfm: not-requested";
    internal void LastFm(string outcome) { lock (gate) lastFm = outcome; }
    private int selectedSeed, selectedRelated, selectedSound, selectedGeneral, soundPreferred, soundAllowance, relatedShortfall, completed;

    internal void Add(string stage, int count)
    {
        lock (gate) discovery[stage] = discovery.GetValueOrDefault(stage) + count;
    }

    internal void Count(string source, string stage, int count = 1)
    {
        lock (gate) counts[(source, stage)] = counts.GetValueOrDefault((source, stage)) + count;
    }

    internal void Reset(params string[] stages)
    {
        lock (gate)
            foreach (var source in Sources)
                foreach (var stage in stages) counts[(source, stage)] = 0;
    }

    internal IDisposable Phase(string name) => new Timing(this, name);

    internal void Selected(IReadOnlyList<MixCandidate> candidates, IReadOnlyList<MixCandidate> selected, int length)
    {
        selectedSound = selected.Count(row => row.Source == "audiomuse");
        selectedSeed = selected.Count(row => row.Source != "audiomuse" && row.RelatedTo != null && row.Song.Artists.Append(row.Song.Artist)
            .Any(artist => string.Equals(artist.Trim(), row.RelatedTo, StringComparison.OrdinalIgnoreCase)));
        selectedRelated = selected.Count(row => row.Source != "audiomuse" && row.RelatedTo != null) - selectedSeed;
        selectedGeneral = selected.Count - selectedSeed - selectedRelated - selectedSound;
        soundPreferred = candidates.Count(row => row.Source == "audiomuse" && row.RelatedTo != null);
        soundAllowance = length * 2 / 5;
        relatedShortfall = Math.Max(0, (int)Math.Ceiling(length * .6) - selectedSeed - selectedRelated);
    }

    internal void Complete() => completed = 1;

    public void Dispose()
    {
        if (logger == null) return;
        lock (gate)
        {
            var stages = string.Join("; ", Sources.Select(source => source + "[" + string.Join(',', Stages.Select(stage =>
                stage + "=" + counts.GetValueOrDefault((source, stage)))) + "]"));
            var phases = string.Join(',', Phases.Select(phase => phase + "=" + timings.GetValueOrDefault(phase))) +
                ",total=" + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            logger.LogInformation("Seed mix candidates: seed resolved={SeedResolved}, seed search found={SeedSearchFound}, provider similar={ProviderSimilar}, artist tracks={ArtistTracks}, related artists={RelatedArtists}, related tracks={RelatedTracks}, similar tracks={SimilarTracks}, suggestions found={SuggestionsFound}, suggestions resolved={SuggestionsResolved}; capped artist tracks={CappedArtistTracks}, provider similar={CappedProviderSimilar}, related tracks={CappedRelatedTracks}, suggestions={CappedSuggestions}, related capped={RelatedCapped}; playback checks={PlaybackChecks}; library access succeeded={LibraryAccessSucceeded}, seed library found={SeedLibraryFound}, related artist names={RelatedArtistNames}; stages {Stages}; sound neighbours={SoundNeighbours}, preferred={Preferred}, radio overlaps={RadioOverlaps}; selected seed/related/sound/general={SelectedSeed}/{SelectedRelated}/{SelectedSound}/{SelectedGeneral}; sound allowance={SoundAllowance}, related shortfall={RelatedShortfall}; {LastFmOutcome}; phases ms {Phases}; completed={Completed}",
                discovery.GetValueOrDefault("seed-resolved"), discovery.GetValueOrDefault("seed-search-found"), discovery.GetValueOrDefault("provider-similar"),
                discovery.GetValueOrDefault("artist-tracks"), discovery.GetValueOrDefault("related-artists"), discovery.GetValueOrDefault("related-tracks"),
                discovery.GetValueOrDefault("similar-tracks"), discovery.GetValueOrDefault("suggestions-found"), discovery.GetValueOrDefault("suggestions-resolved"),
                discovery.GetValueOrDefault("capped-artist-tracks"), discovery.GetValueOrDefault("capped-provider-similar"), discovery.GetValueOrDefault("capped-related-tracks"),
                discovery.GetValueOrDefault("capped-suggestions"), discovery.GetValueOrDefault("related-capped"), discovery.GetValueOrDefault("playback-checks"),
                discovery.GetValueOrDefault("library-access-succeeded"), discovery.GetValueOrDefault("seed-library-found"), discovery.GetValueOrDefault("related-artist-names"),
                stages, discovery.GetValueOrDefault("sound-neighbours"), soundPreferred, discovery.GetValueOrDefault("radio-overlaps"), selectedSeed, selectedRelated, selectedSound, selectedGeneral,
                soundAllowance, relatedShortfall, lastFm, phases, completed);
        }
    }

    private sealed class Timing(SeedMixDiagnostics diagnostics, string name) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            lock (diagnostics.gate) diagnostics.timings[name] = diagnostics.timings.GetValueOrDefault(name) + (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
}
