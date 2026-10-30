using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

/// <summary>Runs the <c>key_rotation</c> cases of <c>managed_native_online_conformance_v1.json</c>.</summary>
public class KeyRotationTests
{
    [Fact]
    public async Task EveryCase()
    {
        var rotation = TestSupport.Fixture("managed_native_online_conformance_v1")["key_rotation"]!;
        var kids = rotation["kids"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!);
        TestSigner root = new(), otherRoot = new();
        var signers = kids.Keys.ToDictionary(k => k, _ => new TestSigner());
        var integration = TestSupport.SyntheticIntegration(root.PublicJwk);
        VerificationManifest Manifest(JsonNode spec, TestSigner signer)
        {
            var keys = new JsonArray();
            foreach (var (name, status) in spec["keys"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var jwk = signers[name].PublicJwk;
                keys.Add(new JsonObject { ["kid"] = kids[name], ["status"] = (string)status!, ["jwk"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = jwk.X, ["y"] = jwk.Y } });
            }
            var payload = new JsonObject
            {
                ["schema"] = "rtfm.product-verification-key-manifest.v1", ["issuer"] = "https://synthetic-license.invalid", ["environment"] = "synthetic",
                ["product_id"] = "synthetic-audio", ["generated_at"] = (string)spec["generated_at"]!, ["keys"] = keys,
            };
            return new VerificationManifest(signer.Sign("synthetic-root-v1", CompactJws.ManifestType, payload), integration);
        }
        var now = DateTimeOffset.FromUnixTimeSeconds(1_785_499_200);
        foreach (var entry in rotation["cases"]!.AsArray())
        {
            var id = (string)entry!["id"]!;
            var expected = entry["expected"]!;
            var held = Manifest(entry["cached"]!, root);
            var fetches = 0;
            DateTimeOffset? last = entry["last_refetch_seconds_ago"] is { } ago ? now.AddSeconds(-(int)ago) : null;
            var outcome = await KeyRotation.ResolveAsync(kids[(string)entry["lease_key"]!], held, last, now, () =>
            {
                fetches++;
                if (entry["refetch"] is not { } spec) throw new HttpRequestException("unreachable");
                return Task.FromResult(Manifest(spec, (string?)spec["signed_by"] == "pinned_root" ? root : otherRoot));
            });
            Assert.True(outcome.Key is not null == (bool)expected["accepted"]!, id);
            Assert.True(fetches == (int)expected["fetches"]!, id);
            Assert.True(outcome.Fetched == fetches > 0, id);
            Assert.True(outcome.Manifest.GeneratedAt == Timestamp.Parse((string)expected["generated_at_after"]!), id);
        }
    }
}
