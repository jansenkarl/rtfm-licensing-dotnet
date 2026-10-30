namespace RTFM.Licensing;

/// <summary>One lease-signing key listed in a Product's root-signed manifest.</summary>
/// <param name="Kid">The key's ID.</param>
/// <param name="Status">Only <c>active</c> keys verify leases; any other status, known or not, does not.</param>
/// <param name="PublicKey">The key.</param>
public sealed record ManifestKey(string Kid, string Status, PublicJwk PublicKey);

/// <summary>A Product's lease-signing keys, accepted only after its signature checks out against the pinned root.</summary>
public sealed record VerificationManifest
{
    /// <summary>When the server made the manifest.</summary>
    public DateTimeOffset GeneratedAt { get; }

    /// <summary>The listed lease-signing keys.</summary>
    public IReadOnlyList<ManifestKey> Keys { get; }

    /// <summary>The signed JWS, kept so the manifest can be stored and verified again on the next launch.</summary>
    public string Jws { get; }

    /// <summary>Verifies the <c>signature</c> field of a <c>GET /v1/products/{code}/verification-keys</c> response (or
    /// a stored JWS).</summary>
    public VerificationManifest(string jws, AppIntegration integration)
    {
        var payload = CompactJws.Verify(jws, integration.RootPublicKey, integration.RootKeyId, CompactJws.ManifestType);
        if (!payload.Property("schema").IsString("rtfm.product-verification-key-manifest.v1")) throw VerificationException.Malformed("manifest schema");
        if (!payload.Property("issuer").IsString(integration.Issuer)) throw VerificationException.WrongScope("manifest issuer");
        if (!payload.Property("environment").IsString(integration.Environment)) throw VerificationException.WrongScope("manifest environment");
        if (!payload.Property("product_id").IsString(integration.ProductCode)) throw VerificationException.WrongScope("manifest product");
        GeneratedAt = Timestamp.Parse(payload.Property("generated_at").StringValue())
            ?? throw VerificationException.Malformed("manifest generated_at");
        if (payload.Property("keys") is not { ValueKind: System.Text.Json.JsonValueKind.Array } entries || entries.GetArrayLength() == 0)
        {
            throw VerificationException.Malformed("manifest keys");
        }
        Keys = entries.EnumerateArray().Select(entry =>
        {
            System.Text.Json.JsonElement? item = entry;
            return item.Value.Property("kid").StringValue() is { Length: > 0 } kid && item.Value.Property("status").StringValue() is { } status
                ? new ManifestKey(kid, status, PublicJwk.FromJson(item.Value.Property("jwk")))
                : throw VerificationException.Malformed("manifest key entry");
        }).ToList();
        Jws = jws;
    }

    internal VerificationManifest(DateTimeOffset generatedAt, IReadOnlyList<ManifestKey> keys, string jws = "")
    {
        GeneratedAt = generatedAt;
        Keys = keys;
        Jws = jws;
    }

    /// <summary>The listed key for <paramref name="kid"/>, or null when the manifest does not list it exactly once.</summary>
    internal ManifestKey? ListedKey(string kid)
    {
        var matching = Keys.Where(key => key.Kid == kid).ToList();
        return matching.Count == 1 ? matching[0] : null;
    }
}
