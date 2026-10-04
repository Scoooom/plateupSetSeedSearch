using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// Folds a finished job's equivalent-seed groups (see SeedSearchResult.EquivalentSeeds) into its
/// match list. During the run job.Equivalents is empty, so this is a no-op passthrough; once the
/// search completes each representative match gains the other seed strings that resolve to the
/// same game. Extracted from SearchEndpoints (which still uses it for GetStatus/ShareSearchResult)
/// so SearchJobManager can also use it when self-persisting a terminal job's CachedSearchResult -
/// Services must not depend on Endpoints, hence the move rather than a cross-reference.
/// </summary>
public static class SearchResultProjection
{
    public static List<SearchMatchDto> WithEquivalents(SearchJob job) =>
        job.Matches
            .Select(m => job.Equivalents.TryGetValue(m.Seed, out var eq)
                ? m with { EquivalentSeeds = eq.Sample, EquivalentCount = eq.Count }
                : m)
            .ToList();
}
