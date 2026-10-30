using System.Globalization;
using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>Reads signed claims by type: booleans, integers and strings stay apart, and an integer must be whole and
/// within the range JSON and JavaScript agree on (the server issues claims from JavaScript).</summary>
internal static class Json
{
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Parses a JSON object, or null when the bytes are not one.</summary>
    public static JsonElement? ParseObject(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    public static JsonElement? Property(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    public static string? StringValue(this JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool IsString(this JsonElement? element, string expected) => element.StringValue() == expected;

    public static bool? BoolValue(this JsonElement? element) => element?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>A whole number within the safe integer range; anything else (fraction, exponent, string, out of
    /// range) is null.</summary>
    public static long? SafeInteger(this JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Number } value) return null;
        var raw = value.GetRawText();
        if (raw.IndexOfAny(['.', 'e', 'E']) >= 0) return null;
        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return null;
        return number is >= -MaxSafeInteger and <= MaxSafeInteger ? number : null;
    }
}

/// <summary>ISO 8601 times as the server writes them (UTC, with or without fractional seconds); a time zone is
/// required.</summary>
internal static partial class Timestamp
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial System.Text.RegularExpressions.Regex Shape();

    public static DateTimeOffset? Parse(string? text) =>
        text is not null && Shape().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToUniversalTime()
            : null;
}
