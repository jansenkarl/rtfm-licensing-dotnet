using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>A P-256 public key in JWK form. A JWK that carries a private part (<c>d</c>) is refused.</summary>
public sealed record PublicJwk
{
    private static readonly BigInteger P = BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger B = BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b", System.Globalization.NumberStyles.HexNumber);

    /// <summary>The x coordinate, base64url.</summary>
    public string X { get; }

    /// <summary>The y coordinate, base64url.</summary>
    public string Y { get; }

    /// <summary>A key from its base64url coordinates; throws unless they are a point on P-256.</summary>
    public PublicJwk(string x, string y)
    {
        var xBytes = Base64Url.Decode(x);
        var yBytes = Base64Url.Decode(y);
        if (xBytes is not { Length: 32 } || yBytes is not { Length: 32 } || !OnCurve(xBytes, yBytes))
        {
            throw VerificationException.Malformed("public key is not a P-256 point");
        }
        X = x;
        Y = y;
    }

    internal static PublicJwk FromJson(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } value || !value.Property("kty").IsString("EC")
            || !value.Property("crv").IsString("P-256") || value.Property("d") is not null
            || value.Property("x").StringValue() is not { } x || value.Property("y").StringValue() is not { } y)
        {
            throw VerificationException.Malformed("public key must be an EC P-256 JWK without a private part");
        }
        return new PublicJwk(x, y);
    }

    internal static PublicJwk FromParameters(ECParameters parameters) =>
        new(Base64Url.Encode(parameters.Q.X), Base64Url.Encode(parameters.Q.Y));

    /// <summary>The JSON the client sends as <c>device_public_key</c>: keys in sorted order, as the server's
    /// <c>stableJson</c>.</summary>
    public string CanonicalJson => $$"""{"crv":"P-256","kty":"EC","x":"{{X}}","y":"{{Y}}"}""";

    /// <summary>The server's device thumbprint for this key: <c>sha256:</c> + base64url(SHA-256(canonical JSON)).</summary>
    public string Thumbprint => "sha256:" + Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson)));

    internal ECDsa CreateVerifier() => ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = Base64Url.Decode(X), Y = Base64Url.Decode(Y) },
    });

    /// <summary>y² = x³ − 3x + b (mod p): the point must lie on P-256.</summary>
    private static bool OnCurve(byte[] x, byte[] y)
    {
        var bx = new BigInteger(x, isUnsigned: true, isBigEndian: true);
        var by = new BigInteger(y, isUnsigned: true, isBigEndian: true);
        if (bx >= P || by >= P) return false;
        var left = BigInteger.ModPow(by, 2, P);
        var right = ((BigInteger.ModPow(bx, 3, P) - 3 * bx + B) % P + P) % P;
        return left == right;
    }
}
