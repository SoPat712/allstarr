namespace allstarr.Tests;

// Keep wall-clock storage budgets independent of unrelated test workloads.
// Concurrency fixtures still create their competing operations explicitly.
[CollectionDefinition(nameof(PlaylistTimingCollection), DisableParallelization = true)]
public sealed class PlaylistTimingCollection;
