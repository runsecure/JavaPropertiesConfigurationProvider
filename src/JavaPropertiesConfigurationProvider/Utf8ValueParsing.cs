using System.Buffers.Text;
using System.Text;

namespace Microsoft.Extensions.Configuration.JavaProperties;

internal static class Utf8ValueParsing
{
    public static string GetString(ReadOnlySpan<byte> span) => span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);

    public static bool GetBoolean(ReadOnlySpan<byte> v)
    {
        if (v.Length is > 0 and <= 5)
        {
            Span<byte> lower = stackalloc byte[5];
            for (int i = 0; i < v.Length; i++)
            {
                byte b = v[i];
                lower[i] = (byte)((b >= (byte)'A' && b <= (byte)'Z') ? b + 32 : b);
            }
            ReadOnlySpan<byte> lowered = lower.Slice(0, v.Length);
            if (lowered.SequenceEqual("true"u8)) return true;
            if (lowered.SequenceEqual("false"u8)) return false;
        }

        throw new FormatException("Value is not a valid boolean.");
    }

    public static int GetInt32(ReadOnlySpan<byte> v)
    {
        if (!Utf8Parser.TryParse(v, out int value, out int consumed) || consumed != v.Length)
        {
            throw new FormatException("Value is not a valid Int32.");
        }
        return value;
    }

    public static long GetInt64(ReadOnlySpan<byte> v)
    {
        if (!Utf8Parser.TryParse(v, out long value, out int consumed) || consumed != v.Length)
        {
            throw new FormatException("Value is not a valid Int64.");
        }
        return value;
    }

    public static double GetDouble(ReadOnlySpan<byte> v)
    {
        if (!Utf8Parser.TryParse(v, out double value, out int consumed) || consumed != v.Length)
        {
            throw new FormatException("Value is not a valid Double.");
        }
        return value;
    }
}
