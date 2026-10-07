namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Minimal Odin Serializer binary-blob reader for SCALAR fields only - NOT a general deserializer.
/// A prior spike at a general Odin decoder failed (see docs/game-domain-knowledge.md's "Odin
/// Serializer blob" section, "could not parse peeked BinaryEntryType byte" - never resolved) and
/// collections (e.g. Dish.RequiredProcesses, a HashSet&lt;Process&gt;) embed a full CLR generic-type
/// name plus a nested per-element protocol this class deliberately does NOT attempt to parse - don't
/// extend this class to read list/HashSet fields without a real decode investigation first.
///
/// Works by finding a field's name as UTF-16LE bytes and reading the fixed-size value immediately
/// following it - validated directly against real assets for exactly three field shapes (see
/// game-domain-knowledge.md's "AddStaple/AddsTheme/AllowRefreshes decode" section for the full
/// writeup and cross-check evidence):
/// - int32 scalar/enum (e.g. ThemeAddEffect.AddsTheme): next 4 bytes, little-endian int32.
/// - bool scalar (e.g. CItemProvider.AllowRefreshes): next 1 byte, nonzero = true.
/// - Unity object reference (e.g. ShopEffect.AddStaple, a direct `Appliance` field): next 4 bytes as
///   a little-endian int32 INDEX into the asset's own serializationData.ReferencedUnityObjects list
///   (NOT a raw value) - resolve via AssetYamlReader.ReferencedUnityObjectGuids()[index]. The
///   sentinel value 1376535 (confirmed identical across 10 real unset-reference cases) means
///   "null/unset" - TryReadReferenceIndex already filters this out.
/// </summary>
public sealed class OdinFieldReader
{
    private const int NullReferenceSentinel = 1376535;

    private readonly byte[] _raw;

    private OdinFieldReader(byte[] raw) => _raw = raw;

    public static OdinFieldReader? FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return null;
        }

        return new OdinFieldReader(Convert.FromHexString(hex));
    }

    public bool TryReadInt32(string fieldName, out int value)
    {
        var offset = FindFieldValueOffset(fieldName);
        if (offset is null || offset.Value + 4 > _raw.Length)
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToInt32(_raw, offset.Value);
        return true;
    }

    public bool TryReadBool(string fieldName, out bool value)
    {
        var offset = FindFieldValueOffset(fieldName);
        if (offset is null || offset.Value >= _raw.Length)
        {
            value = false;
            return false;
        }

        value = _raw[offset.Value] != 0;
        return true;
    }

    /// <summary>
    /// Reads a Unity-object-reference field's ReferencedUnityObjects index. Returns null if the
    /// field is absent on this asset (no effect/component of that type present) OR holds the
    /// null-reference sentinel (effect present but reference unset) - callers don't need to
    /// special-case the sentinel themselves.
    /// </summary>
    public int? TryReadReferenceIndex(string fieldName)
    {
        if (!TryReadInt32(fieldName, out var index) || index == NullReferenceSentinel)
        {
            return null;
        }

        return index;
    }

    private int? FindFieldValueOffset(string fieldName)
    {
        var needle = System.Text.Encoding.Unicode.GetBytes(fieldName);
        var index = IndexOf(_raw, needle);
        return index < 0 ? null : index + needle.Length;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
