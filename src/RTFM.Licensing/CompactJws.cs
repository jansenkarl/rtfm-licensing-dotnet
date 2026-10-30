using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>ES256 compact JWS as issued by the RTFM licensing server: <c>header.payload.signature</c>, raw r||s
/// signature.</summary>
internal static class CompactJws
{
    public const string LeaseType = "rtfm.license-lease.v1+jws";
    public const string TrialLeaseType = "rtfm.trial-lease.v1+jws";
    public const string ManifestType = "rtfm.product-verification-key-manifest.v1+jws";

    /// <summary>The header's <c>kid</c>, read before the signature is checked, only to choose which key to check it
    /// with.</summary>
    public static string UnverifiedKeyId(string jws)
    {
        var parts = Split(jws);
        var kid = DecodeObject(parts[0], "header").Property("kid").StringValue();
        if (string.IsNullOrEmpty(kid)) throw new VerificationException(VerificationFailure.UnexpectedHeader, "kid");
        return kid;
    }

    /// <summary>The payload, unverified, for reading a stored lease's timing only (it was verified when accepted).</summary>
    public static JsonElement? UnverifiedPayload(string jws)
    {
        var parts = jws.Split('.');
        return parts.Length == 3 && Base64Url.Decode(parts[1]) is { } bytes ? Json.ParseObject(bytes) : null;
    }

    /// <summary>Checks the header (<c>alg</c>, <c>kid</c>, <c>typ</c>) and the signature, then returns the payload.</summary>
    public static JsonElement Verify(string jws, PublicJwk key, string kid, string type)
    {
        var parts = Split(jws);
        var header = DecodeObject(parts[0], "header");
        if (!header.Property("alg").IsString("ES256")) throw new VerificationException(VerificationFailure.UnexpectedHeader, "alg");
        if (!header.Property("kid").IsString(kid)) throw new VerificationException(VerificationFailure.UnexpectedHeader, "kid");
        if (!header.Property("typ").IsString(type)) throw new VerificationException(VerificationFailure.UnexpectedHeader, "typ");
        var signature = Base64Url.Decode(parts[2]);
        using var verifier = key.CreateVerifier();
        if (signature is not { Length: 64 } || !verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new VerificationException(VerificationFailure.UntrustedSignature);
        }
        return DecodeObject(parts[1], "payload");
    }

    private static string[] Split(string jws)
    {
        var parts = jws.Split('.');
        if (parts.Length != 3) throw VerificationException.Malformed("expected a compact JWS");
        return parts;
    }

    private static JsonElement DecodeObject(string part, string label) =>
        Base64Url.Decode(part) is { } bytes && Json.ParseObject(bytes) is { } value
            ? value
            : throw VerificationException.Malformed($"JWS {label} must be a JSON object");
}
