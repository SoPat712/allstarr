namespace allstarr.Core.Music;

public sealed record MixRecipe(string Version, string Sentence, string Inputs, string Filters, string Order,
    int Target, int ArtistLimit, string Cadence, string Shortfall, int WindowDays = 0, int FallbackDays = 0, int MinimumPlays = 0, int InactiveMonths = 0);
public sealed record MixRecipeEvidence(MixRecipe Recipe, int LivePlays, int ImportedPlays,
    DateTimeOffset? ListeningFrom, DateTimeOffset? ListeningTo, string? HistoricalWindow,
    int PrimaryPlays, int FallbackPlays, int Candidates, int Qualified, int Shown, int FromMusic, int FromServices,
    IReadOnlyDictionary<string, int> Excluded, int? ProviderReads, int? ProviderFailures, string? Shortfall, int? ReleaseInboxEntries = null, string? TagChartStatus = null);

internal static class MixRecipes
{
    internal const int RecipeReads = 30, GenerationReads = 240, HistoryLookupReads = 60;
    internal const int RecentDays = 30, RotationDays = 90, LostPlays = 5, LostMonths = 6, ReleaseDays = 14, FrequentPlays = 10, TopArtists = 20, TopStyles = 10;
    private const string Common = "Leaves out songs you can't play, explicit songs on clean-only accounts, anything you marked Never, songs you skipped early 3 times in 30 days, and repeats. Songs you marked Less come later.";
    internal static MixRecipe For(string kind, string? style = null) => kind switch
    {
        "on-repeat" => new("rotation-v1", $"Your most-played songs in {RecentDays} days, filled from {RotationDays} days.",
            "Completed plays, including enabled imports.", Common,
            "Most plays in the last 30 days first, then the last 90 days.",
            50, 2, "Updates every day", "Keep the eligible songs when fewer than 50 qualify. Under 30 live plays, use the latest imported 30-day window; its actual dates are shown.", WindowDays: RecentDays, FallbackDays: RotationDays),
        "rewind" => new("lost-v1", $"Songs played at least {LostPlays} times, with no play in {LostMonths} months.",
            "Full retained completed-play history, including enabled imports.", Common + " At least 5 plays; last play at least 6 months ago; hearts do not replace plays.",
            "Most plays first. Songs you marked Less come later.", 50, 3, "Updates every Monday", "Keep the eligible songs when fewer than 50 qualify.", MinimumPlays: LostPlays, InactiveMonths: LostMonths),
        "discover-weekly" => new("uncharted-v1", $"Unplayed songs by artists related to your top {TopArtists} played artists; known styles must include one of your top {TopStyles}.",
            "Completed plays, canonical styles, provider artist relationships.", Common + " Never played or hearted; related artist, with a shared top style when styles are known; no pick repeated within 14 days.",
            "Songs from your music first, then your services.", 50, 2, "Updates every Monday", "Keep only evidenced relationships and styles; keep the eligible songs if fewer than 50 qualify."),
        "release-radar" => new("releases-v1", $"Releases from the last {ReleaseDays} days by followed artists or artists played at least {FrequentPlays} times.",
            "Your release inbox and completed plays.", Common + " One playable track per release, in track order.",
            "Newest releases first, then track order.", 50, 2, "Updates every Friday", "Keep eligible releases; do not invent missing dates or releases.", WindowDays: ReleaseDays, MinimumPlays: FrequentPlays),
        "custom" => new("seed-v2", "Songs selected from your saved seed.", "Selected song or artist, related artists, available sound analysis and existing radio suggestions.",
            Common, "Seed artist and radio suggestions interleaved with similar sound; sound fills shortfalls before general favourites.",
            50, 2, "Stays as saved", "Use extra sound neighbours when related artists run short, then top up from favourites and your music."),
        "external" => new("linked-v1", "Your music service chooses the source songs and their order; Allstarr matches playable versions and applies your listening policy.",
            "Saved linked playlist snapshot.", "Playable matches and household listening policy.", "Source order, including source duplicates.",
            0, 0, "Saved source sync schedule", "Unmatched and unavailable songs are not shown; the source's private selection rules are unknown."),
        _ when kind.StartsWith("daily-", StringComparison.Ordinal) => new("blend-v1", $"Half familiar songs and half unplayed songs in {style ?? "your first known style"}, from artists you already play.",
            "Completed plays, hearts and canonical styles.", Common + " Saved track style or a style on that played artist’s own songs; unplayed picks must be from already-played artists.",
            "Familiar songs with the most plays first, then songs you have not played. Songs you marked Less come later.", 50, 2, "Updates every day",
            "Up to 25 familiar and 25 unplayed songs; keep either side's shortfall without borrowing from the other."),
        _ when PersonalMixMoods.Kinds.Contains(kind) => new(PersonalMixMoods.RecipeVersion, PersonalMixMoods.Rule(kind),
            "Your music, saved tags, available sound analysis, and evidenced service picks.", Common + " " + PersonalMixMoods.Rule(kind),
            "Songs from your music first, then your services, with the most plays first. Songs you marked Less come later.",
            40, 2, "Updates every Monday", "Keep qualifying songs when fewer than 40 are available; at least 25 make the mix ready."),
        _ => throw new ArgumentException("Unknown mix recipe.")
    };
}
