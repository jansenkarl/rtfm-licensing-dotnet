using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>A signed entitlement value: text, a whole number, a yes/no, or a date and time.</summary>
public abstract record EntitlementValue
{
    private EntitlementValue() { }

    /// <summary>A string value.</summary>
    public sealed record Text(string Value) : EntitlementValue;

    /// <summary>A whole number.</summary>
    public sealed record Integer(long Value) : EntitlementValue;

    /// <summary>A checkbox value.</summary>
    public sealed record Boolean(bool Value) : EntitlementValue;

    /// <summary>A date and time.</summary>
    public sealed record DateTime(DateTimeOffset Value) : EntitlementValue;
}

/// <summary>A signed entitlement from a verified lease: system values such as <c>activation.limit</c>, and the
/// publisher's custom license fields.</summary>
/// <param name="Code">The field's code, such as <c>edition</c>.</param>
/// <param name="Value">Its value.</param>
public sealed record Entitlement(string Code, EntitlementValue Value)
{
    /// <summary>Typed entries (<c>type</c> present) are required once a lease carries <c>entitlement_revision</c>; older
    /// leases carry untyped <c>{code, value}</c> entries, whose type follows the JSON value.</summary>
    internal static IReadOnlyList<Entitlement> ParseList(JsonElement? json, bool typed)
    {
        if (json is not { ValueKind: JsonValueKind.Array } items || items.GetArrayLength() == 0) throw VerificationException.Malformed("entitlements");
        var seen = new HashSet<string>();
        var list = new List<Entitlement>();
        foreach (var entry in items.EnumerateArray())
        {
            JsonElement? item = entry;
            if (item.Value.ValueKind != JsonValueKind.Object || item.Value.Property("code").StringValue() is not { Length: > 0 } code
                || !seen.Add(code) || item.Value.Property("value") is not { } raw)
            {
                throw VerificationException.Malformed("entitlement entry");
            }
            var type = item.Value.Property("type");
            if (typed)
            {
                if (type.StringValue() is not { } name) throw VerificationException.Malformed($"entitlement {code} type");
                list.Add(new Entitlement(code, TypedValue(name, raw, code)));
                continue;
            }
            if (type is not null) throw VerificationException.Malformed($"entitlement {code} type without a revision");
            JsonElement? value = raw;
            list.Add(new Entitlement(code, raw.ValueKind switch
            {
                JsonValueKind.String => new EntitlementValue.Text(raw.GetString()!),
                JsonValueKind.True or JsonValueKind.False => new EntitlementValue.Boolean(raw.GetBoolean()),
                _ => value.SafeInteger() is { } number ? new EntitlementValue.Integer(number)
                    : throw VerificationException.Malformed($"entitlement {code} value"),
            }));
        }
        return list;
    }

    private static EntitlementValue TypedValue(string type, JsonElement raw, string code)
    {
        JsonElement? value = raw;
        EntitlementValue? result = type switch
        {
            "string" when raw.ValueKind == JsonValueKind.String => new EntitlementValue.Text(raw.GetString()!),
            "boolean" when value.BoolValue() is { } flag => new EntitlementValue.Boolean(flag),
            "datetime" when Timestamp.Parse(value.StringValue()) is { } date => new EntitlementValue.DateTime(date),
            "integer" when value.SafeInteger() is { } number => new EntitlementValue.Integer(number),
            _ => null,
        };
        return result ?? throw VerificationException.Malformed($"entitlement {code} is not a valid {type}");
    }
}
