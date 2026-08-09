namespace PlateUpSeedTools.Infrastructure.Cache;

public sealed class CacheOptions
{
    public const string SectionName = "CacheStore";

    public required string SqlitePath { get; init; }
}
