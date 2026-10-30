namespace RTFM.Licensing;

/// <summary>Strict unpadded base64url, as the licensing server writes it.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The bytes, or null for anything that is not strict unpadded base64url.</summary>
    public static byte[]? Decode(string text)
    {
        if (text.Length % 4 == 1) return null;
        foreach (var c in text)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
        }
        var padded = text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4);
        try { return Convert.FromBase64String(padded); } catch (FormatException) { return null; }
    }
}
