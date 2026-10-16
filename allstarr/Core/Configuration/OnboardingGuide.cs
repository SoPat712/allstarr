using System.Data;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Configuration;

public sealed record SetupGuideStep(string Id, string Title, string Explanation, string? Href, bool Required, string State = "pending");

public static class SetupGuide
{
    public static IReadOnlyList<SetupGuideStep> Steps { get; } = Array.AsReadOnly<SetupGuideStep>(
    [
        new(OnboardingStateService.BackendIdentityStep, "Your media server", "Your verified media-server account owns library access. Native playback keeps working while you review setup.", null, true),
        new(OnboardingStateService.LegacyEnvironmentStep, "Review previous settings", "Preview the old environment before importing. Existing saved settings win, credentials stay encrypted, and Valkey-only favorites and manual fixes are left behind.", "#/settings/setup", false),
        new("original-media", "Keep your existing songs", "Original kept and permanent-download files are verified in place without moving, retagging or deleting them. Watch the import job in Activity; changed or missing files need review.", "#/library/kept", false),
        new("sources", "Choose your sources", "SquidWTF is retired. Choose which services you want; no replacement is selected automatically. Imported accounts are enabled only after a successful connection check.", "#/integrations/services", false),
        new("routing-quality", "Set source order and quality", "Review metadata, playback, download and lyrics order. Local songs keep their native access rules. Choose a quality ceiling and whether lower-quality fallback is acceptable.", "#/integrations/routing", false),
        new("playlists", "Review playlists", "Choose import once or keep in sync, into a new or existing playlist. Source order is preserved, and synchronization only removes entries Allstarr added.", "#/library/playlists", false),
        new("matching", "Review matches", "Known recording identities preserve external IDs. Review uncertain matches and saved overrides before relying on an imported playlist.", "#/library/mappings", false),
        new("lyrics-kept", "Lyrics and kept songs", "Choose your lyrics sources. Hearts and retained playlists can keep household copies; imported originals remain read-only and receive no new sidecars.", "#/integrations/routing", false),
        new("scrobbling", "Listening history and scrobbling", "Connect only the history destinations you want. Uncertain deliveries stay visible for verification rather than silently sending duplicates.", "#/integrations/accounts", false),
        new("listeners", "Listener choices", "Backend roles remain authoritative. Review whether listeners may connect personal accounts and which household defaults they can override.", "#/settings", false),
        new("apple", "Optional Apple downloads", "Apple downloads are off by default. The account can work without the add-on. To enable downloads, verify the package in Sources, run the displayed host command, then finish its connection checks.", "#/integrations/services/apple-musickit", false),
        new("deployment", "Finish the upgrade", "Download a verified backup and preserve your original folders. After checking the saved settings, tidy obsolete deployment entries. Keep the previous image and matching backup for rollback.", "#/settings/backups", false)
    ]);
}

public sealed partial class OnboardingStateService
{
    public async Task<OnboardingStateSnapshot> SetStepAsync(Guid userId, string stepId, string action,
        long expectedRevision, string correlationId, CancellationToken cancellationToken = default)
    {
        var step = SetupGuide.Steps.SingleOrDefault(item => item.Id == stepId)
            ?? throw new OnboardingStateException("unknown_step", "Choose a step from the current setup guide.");
        if (action is not ("review" or "skip" or "reset"))
            throw new OnboardingStateException("invalid_step_action", "Choose Review, Skip or Reset for this step.");
        if (step.Required && action != "review")
            throw new OnboardingStateException("required_step", "The verified media-server identity cannot be skipped or reset.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await HasIdentityAsync(db, userId, cancellationToken))
            throw new OnboardingStateException("backend_identity_required", "Sign in with an active media-server administrator before saving setup.");
        var current = await db.OnboardingStates.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if ((current?.Revision ?? 0) != expectedRevision)
            throw new OnboardingStateException("setup_changed", "Setup changed in another tab. Reload the guide and try again.");
        var state = current ?? await GetOrCreateAsync(db, userId, "setup-guide", clock.UtcNow, cancellationToken);
        var steps = ReadSteps(state).Where(item => item != stepId && item != "skip:" + stepId).ToList();
        if (action != "reset") steps.Add(action == "skip" ? "skip:" + stepId : stepId);
        SetSteps(state, steps);
        state.SchemaVersion = SchemaVersion;
        state.UpdatedAt = clock.UtcNow;
        state.Revision++;
        AddAudit(db, state, userId, correlationId, "onboarding.step");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Snapshot(state, identityPresent: true);
    }
}
