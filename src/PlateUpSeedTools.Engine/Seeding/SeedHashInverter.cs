namespace PlateUpSeedTools.Engine.Seeding;

/// <summary>
/// Finds a seed string that hashes (via <see cref="FixedString32Hash"/>) to a given target
/// <see cref="Seed.IntValue"/>, using meet-in-the-middle instead of brute force.
///
/// FixedString32Hash's djb2 recurrence is linear mod 2^32 in the per-character byte values:
/// processing bytes[length-1] first down to bytes[0] last gives
///   targetHash = 5381*33^length + Σ_{k=0}^{length-1} bytes[k]*33^k  (mod 2^32)
/// where bytes[0] is the FIRST character of the string (lowest weight, 33^0) and
/// bytes[length-1] is the LAST character (highest weight, 33^(length-1)) - the hash loop walks
/// the string in reverse, so the first character ends up applied last.
///
/// This makes "find a string of a given length, drawn from a small alphabet, that hashes to a
/// target value" a bounded knapsack problem mod 2^32: split the character positions into two
/// halves, enumerate every combination for each half (alphabet^(length/2) each, ~130K for the
/// default 8-character/19-letter case), and meet in the middle via a hash-table lookup instead
/// of enumerating the full alphabet^length space.
/// </summary>
public static class SeedHashInverter
{
    /// <summary>
    /// Returns a string of exactly <paramref name="length"/> characters drawn from
    /// <paramref name="alphabet"/> whose <see cref="FixedString32Hash.ComputeHashCode"/> equals
    /// <paramref name="targetHash"/>, or null if no such string exists (the hash is many-to-one,
    /// so not every 32-bit value has a preimage of a given length/alphabet).
    /// </summary>
    public static string? FindPreimage(int targetHash, int length, string alphabet = Seed.FriendlyCharacters)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length must be positive.");
        }

        var target = unchecked((uint)targetHash);
        var pow = Pow33Table(length);
        var baseTerm = unchecked(5381u * pow[length]);
        var mid = length / 2;

        var lowTable = BuildWeightedSumTable(alphabet, mid, pow, weightOffset: 0);

        foreach (var highCombo in EnumerateCombinations(alphabet, length - mid))
        {
            var highSum = WeightedSum(highCombo, pow, weightOffset: mid);
            var needed = unchecked(target - baseTerm - highSum);
            if (lowTable.TryGetValue(needed, out var lowCombo))
            {
                return lowCombo + highCombo;
            }
        }

        return null;
    }

    private static Dictionary<uint, string> BuildWeightedSumTable(string alphabet, int comboLength, uint[] pow, int weightOffset)
    {
        var table = new Dictionary<uint, string>();
        foreach (var combo in EnumerateCombinations(alphabet, comboLength))
        {
            // First representative for a given sum wins - collisions within one half are expected
            // and any valid representative works equally well as part of a full-length preimage.
            table.TryAdd(WeightedSum(combo, pow, weightOffset), combo);
        }

        return table;
    }

    private static uint WeightedSum(string combo, uint[] pow, int weightOffset)
    {
        var sum = 0u;
        for (var k = 0; k < combo.Length; k++)
        {
            sum = unchecked(sum + (uint)combo[k] * pow[weightOffset + k]);
        }

        return sum;
    }

    private static uint[] Pow33Table(int length)
    {
        var pow = new uint[length + 1];
        pow[0] = 1;
        for (var i = 1; i <= length; i++)
        {
            pow[i] = unchecked(pow[i - 1] * 33);
        }

        return pow;
    }

    /// <summary>Every string of exactly <paramref name="length"/> characters drawn from <paramref name="alphabet"/>, odometer order.</summary>
    private static IEnumerable<string> EnumerateCombinations(string alphabet, int length)
    {
        if (length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var indices = new int[length];
        var chars = new char[length];
        while (true)
        {
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[indices[i]];
            }

            yield return new string(chars);

            var pos = length - 1;
            while (pos >= 0)
            {
                indices[pos]++;
                if (indices[pos] < alphabet.Length)
                {
                    break;
                }

                indices[pos] = 0;
                pos--;
            }

            if (pos < 0)
            {
                yield break;
            }
        }
    }
}
