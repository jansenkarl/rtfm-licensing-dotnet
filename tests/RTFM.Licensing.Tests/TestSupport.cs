using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

internal static class TestSupport
{
    /// <summary>Reads a shared fixture file.</summary>
    public static JsonNode Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!;

    public static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    /// <summary>An App integration file for the synthetic publisher in the managed conformance fixture.</summary>
    public static AppIntegration SyntheticIntegration(PublicJwk root, string rootKeyId = "synthetic-root-v1",
        string? tenantId = "tenant_synthetic_publisher", string productVersionCode = "synthetic-audio-1")
    {
        var file = new Dictionary<string, object>
        {
            ["schema"] = "rtfm.app-integration.v1", ["environment"] = "synthetic", ["apiBaseUrl"] = "https://synthetic-license.invalid",
            ["productCode"] = "synthetic-audio", ["productVersionCode"] = productVersionCode, ["rootKeyId"] = rootKeyId,
            ["rootPublicJwk"] = new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = root.X, ["y"] = root.Y },
        };
        if (tenantId is not null) file["tenantId"] = tenantId;
        return new AppIntegration(Json(file));
    }
}

/// <summary>An ephemeral P-256 signer standing in for the server in tests.</summary>
internal sealed class TestSigner
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public PublicJwk PublicJwk => PublicJwk.FromParameters(key.ExportParameters(false));

    public Dictionary<string, string> JwkObject => new() { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = PublicJwk.X, ["y"] = PublicJwk.Y };

    public string Sign(string kid, string type, object payload)
    {
        var header = TestSupport.Json(new Dictionary<string, string> { ["alg"] = "ES256", ["kid"] = kid, ["typ"] = type });
        var input = Base64Url.Encode(header) + "." + Base64Url.Encode(payload is JsonNode node ? Encoding.UTF8.GetBytes(node.ToJsonString()) : TestSupport.Json(payload));
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return input + "." + Base64Url.Encode(signature);
    }
}
