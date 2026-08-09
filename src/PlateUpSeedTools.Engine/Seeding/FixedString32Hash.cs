using System.Text;

namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Port of Unity.Collections.FixedString32.GetHashCode(), which delegates to
/// FixedStringMethods.ComputeHashCode -&gt; CollectionHelper.Hash(byte*, int).
/// Decompiled directly from Unity.Collections.dll (PlateUpSourceFiles/CompiledRawGame/PlateUp_Data/Managed/),
/// not inferred from a community port - this is the djb2 hash (multiplier 33, seed 5381) over the
/// string's UTF8 bytes, walked in REVERSE byte order:
///
///   ulong hash = 5381;
///   while (bytes > 0) { byte b = ptr[--bytes]; hash = (hash &lt;&lt; 5) + hash + b; }
///   return (uint)hash;
///
/// Seed strings accepted by the web API are restricted to ASCII alphanumeric characters (see
/// LayoutEndpoints), all single-byte in UTF8, so a plain Encoding.UTF8.GetBytes is faithful here -
/// no need to handle multi-byte UTF8 sequences. (The real game's Seed constructor itself has no
/// such restriction - see Seed.cs's doc comment - but nothing beyond ASCII alphanumeric is expected
/// to reach this method in practice.)
///
/// Cross-checked against real Seed.IntValue values captured from the actual running game via a
/// BepInEx logger plugin (PlateUpSourceFiles/GroundTruthLogger/) - all matched exactly, see
/// FixedString32HashTests.
/// </summary>
public static class FixedString32Hash
{
    public static int ComputeHashCode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = 5381UL;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            hash = (hash << 5) + hash + bytes[i];
        }

        return unchecked((int)(uint)hash);
    }
}
