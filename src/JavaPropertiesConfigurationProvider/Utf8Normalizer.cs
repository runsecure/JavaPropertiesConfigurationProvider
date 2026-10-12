using System.Text;

namespace Microsoft.Extensions.Configuration.JavaProperties;

internal enum ByteOrderMark
{
    None,
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian
}

/// <summary>
/// Detects and normalizes byte-order-mark-prefixed input (UTF-8, UTF-16 LE, UTF-16 BE) to plain,
/// BOM-free UTF-8 so it can be fed to <see cref="JavaPropertiesReader"/>, which only understands UTF-8.
/// </summary>
internal static class Utf8Normalizer
{
    public static ByteOrderMark DetectBom(ReadOnlySpan<byte> head, out int length)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            length = 3;
            return ByteOrderMark.Utf8;
        }
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            length = 2;
            return ByteOrderMark.Utf16LittleEndian;
        }
        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            length = 2;
            return ByteOrderMark.Utf16BigEndian;
        }

        length = 0;
        return ByteOrderMark.None;
    }

    /// <summary>Strips a UTF-8 BOM or transcodes UTF-16 (LE/BE) input to UTF-8. Plain UTF-8 input is returned unchanged.</summary>
    public static byte[] Normalize(byte[] raw)
    {
        ByteOrderMark bom = DetectBom(raw, out int length);
        return bom switch
        {
            ByteOrderMark.Utf8 => raw.AsSpan(length).ToArray(),
            ByteOrderMark.Utf16LittleEndian => Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(raw, length, raw.Length - length)),
            ByteOrderMark.Utf16BigEndian => Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(raw, length, raw.Length - length)),
            _ => raw
        };
    }
}
