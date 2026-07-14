using allstarr.Core.Intelligence;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Storage;
public sealed partial class AllstarrDbContext
{
    public DbSet<IntelligencePolicyRecord> IntelligencePolicies => Set<IntelligencePolicyRecord>();
    public DbSet<ListeningSignalRecord> ListeningSignals => Set<ListeningSignalRecord>();
    public DbSet<ListeningProfileRecord> ListeningProfiles => Set<ListeningProfileRecord>();
    public DbSet<RecommendationRunRecord> RecommendationRuns => Set<RecommendationRunRecord>();
    public DbSet<RecommendationCandidateRecord> RecommendationCandidates => Set<RecommendationCandidateRecord>();
    public DbSet<GeneratedSetRecord> GeneratedSets => Set<GeneratedSetRecord>();
    public DbSet<GeneratedSetEntryRecord> GeneratedSetEntries => Set<GeneratedSetEntryRecord>();
}
