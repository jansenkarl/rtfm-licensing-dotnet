using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

/// <summary>Runs every case in <c>managed_native_online_conformance_v1.json</c> through the SDK's verifier, the same
/// cases the server repository and the Swift SDK run.</summary>
public class ManagedConformanceTests
{
    [Fact]
    public void EveryCase()
    {
        var data = TestSupport.Fixture("managed_native_online_conformance_v1");
        var config = data["public_integration"]!.AsObject();
        var claims = data["signed_paid_claims"]!.AsObject();
        var cases = data["cases"]!.AsArray();
        Assert.Equal(cases.Count, cases.Select(c => (string)c!["id"]!).Distinct().Count());
        TestSigner root = new(), lease = new(), other = new();
        foreach (var entry in cases)
        {
            var id = (string)entry!["id"]!;
            Assert.True(Check(entry.AsObject(), config, claims, root, lease, other) == (bool)entry["accepted"]!, id);
        }
    }

    private static bool Check(JsonObject entry, JsonObject config, JsonObject baseClaims, TestSigner root, TestSigner lease, TestSigner other)
    {
        var change = (string)entry["change"]!;
        var at = (long)entry["at"]!;
        var leaseKid = change == "unknown_kid" ? "unknown-lease-key" : (string)config["lease_kid"]!;
        var keys = new JsonArray { new JsonObject { ["kid"] = (string)config["lease_kid"]!, ["status"] = "active", ["jwk"] = Jwk(lease) } };
        if (change is "rotated_kid" or "unknown_kid") keys.Insert(0, new JsonObject { ["kid"] = "retired-key", ["status"] = "retired", ["jwk"] = Jwk(other) });
        var manifest = new JsonObject
        {
            ["schema"] = "rtfm.product-verification-key-manifest.v1",
            ["issuer"] = change == "wrong_issuer" ? "https://other.invalid" : (string)config["issuer"]!,
            ["environment"] = change == "wrong_environment" ? "other" : (string)config["environment"]!,
            ["product_id"] = change == "manifest_product" ? "other-product" : (string)config["product_id"]!,
            ["generated_at"] = "2026-07-29T12:00:00.000Z", ["keys"] = keys,
        };
        var claims = baseClaims.DeepClone().AsObject();
        claims["iss"] = change == "wrong_lease_issuer" ? "https://other.invalid" : (string)config["issuer"]!;
        claims["tenant_id"] = (string)config["tenant_id"]!;
        claims["product_id"] = (string)config["product_id"]!;
        claims["release_family_id"] = (string)config["release_family_id"]!;
        claims["device_thumbprint"] = (string)config["device_thumbprint"]!;
        claims["kid"] = leaseKid;
        claims["license_id"] = "lic_synthetic";
        claims["activation_id"] = "act_synthetic";
        if (change == "malformed_entitlement_type" && claims["entitlements"] is JsonArray list)
        {
            list[1] = new JsonObject { ["code"] = "activation.limit", ["type"] = "integer", ["value"] = "3" };
        }
        var leaseJws = lease.Sign(leaseKid, CompactJws.LeaseType, claims);
        if (change == "tamper_lease_signature")
        {
            var parts = leaseJws.Split('.');
            leaseJws = parts[0] + "." + parts[1] + "." + (parts[2].StartsWith('A') ? "B" : "A") + parts[2][1..];
        }
        var integration = TestSupport.SyntheticIntegration(change == "untrusted_root" ? other.PublicJwk : root.PublicJwk,
            tenantId: change == "tenant_scope" ? "other-tenant" : (string?)config["tenant_id"],
            productVersionCode: change == "version_scope" ? "other-version" : (string)config["release_family_id"]!);
        var highWater = new HighWater
        {
            Sequence = change == "sequence_high_water" ? 3 : 2, RevocationEpoch = change == "stale_revocation_epoch" ? 1 : 0,
            ObservedTime = change == "clock_high_water" ? at + 1 : at,
        };
        var expectations = new LeaseExpectations(integration, change == "device_scope" ? "other-device" : (string)config["device_thumbprint"]!, highWater);
        try
        {
            var verified = new VerificationManifest(root.Sign((string)config["root_kid"]!, CompactJws.ManifestType, manifest), integration);
            var result = LeaseVerifier.Verify(leaseJws, LeaseType.Paid, verified, expectations);
            Assert.Equal([new Entitlement("app.major_family", new EntitlementValue.Text("synthetic-audio-1")),
                new Entitlement("activation.limit", new EntitlementValue.Integer(3)), new Entitlement("feature.spatial", new EntitlementValue.Boolean(true))],
                result.Entitlements);
            // The fixture labels a failed durable commit as a decision input; the store is tested separately.
            if (change == "durable_commit_failure") return false;
            var network = (string)entry["outcome"]! switch
            {
                "network_unavailable" => NetworkState.Unavailable,
                "available" => NetworkState.Available,
                _ => NetworkState.NotChecked,
            };
            var decision = AccessTiming.Decide(result, at, highWater.ObservedTime, network);
            return decision is AccessDecision.Valid or AccessDecision.Grace;
        }
        catch (VerificationException) { return false; }
    }

    private static JsonObject Jwk(TestSigner signer) => new() { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = signer.PublicJwk.X, ["y"] = signer.PublicJwk.Y };
}
