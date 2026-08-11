using System.Security.Cryptography;

namespace PlateUpSeedTools.Web;

/// <summary>
/// Short, opaque, random lookup keys for shareable links (search results, Explorer preloads) - a
/// hash used purely as a DB key, never an encoding of the shared data itself (the data lives in an
/// ICacheStore&lt;T&gt; row, looked up by this token - see SearchEndpoints' /api/search/{jobId}/share
/// and ExplorerEndpoints' /api/explorer/preload).
/// </summary>
public static class ShareHash
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int Length = 16;

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);
}
