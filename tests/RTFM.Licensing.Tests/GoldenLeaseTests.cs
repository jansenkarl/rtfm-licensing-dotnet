using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

/// <summary>Verifies leases signed by the server's own JWS code (the golden fixtures), so the SDK and the server agree
/// on encoding, signatures and claims.</summary>
public class GoldenLeaseTests
{
    private static (string Jws, string Kid, PublicJwk Key, JsonNode Payload) Golden(string name)
    {
        var data = TestSupport.Fixture(name);
        var jwk = data["public_jwk"]!;
        return ((string)data["jws"]!, (string)data["key_id"]!, new PublicJwk((string)jwk["x"]!, (string)jwk["y"]!), data["expected_payload"]!);
    }

    private static AppIntegration Staging(string tenant = "tenant_rtfm_design")
    {
        var root = new TestSigner().PublicJwk;
        return new AppIntegration(TestSupport.Json(new Dictionary<string, object>
        {
            ["schema"] = "rtfm.app-integration.v1", ["environment"] = "staging", ["apiBaseUrl"] = "https://license-api-staging.rtfmdesign.com",
            ["productCode"] = "rtfm-player", ["productVersionCode"] = "rtfm-1", ["rootKeyId"] = "root",
            ["rootPublicJwk"] = new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = root.X, ["y"] = root.Y }, ["tenantId"] = tenant,
        }));
    }

    private static Lease Verify(string name, LeaseType kind, string tenant = "tenant_rtfm_design")
    {
        var golden = Golden(name);
        var manifest = new VerificationManifest(DateTimeOffset.UtcNow, [new ManifestKey(golden.Kid, "active", golden.Key)]);
        var expectations = new LeaseExpectations(Staging(tenant), (string)golden.Payload["device_thumbprint"]!,
            new HighWater { Sequence = 1, TrialRevision = 1 });
        return LeaseVerifier.Verify(golden.Jws, kind, manifest, expectations);
    }

    [Fact]
    public void ServerSignedPayloadsDecodeExactly()
    {
        foreach (var name in new[] { "paid_lease_valid_p256_es256", "paid_lease_custom_entitlements_valid_p256_es256", "trial_lease_valid_p256_es256" })
        {
            var golden = Golden(name);
            var type = name.StartsWith("trial") ? CompactJws.TrialLeaseType : CompactJws.LeaseType;
            var payload = CompactJws.Verify(golden.Jws, golden.Key, golden.Kid, type);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload.GetRawText()), golden.Payload), name);
        }
    }

    [Fact]
    public void PaidLease()
    {
        var lease = Verify("paid_lease_valid_p256_es256", LeaseType.Paid);
        Assert.Equal(new LeaseKind.Paid("lic_golden_paid_001", "act_golden_paid_001", 0), lease.Kind);
        Assert.Equal([new Entitlement("app.major_family", new EntitlementValue.Text("rtfm-1")), new Entitlement("activation.limit", new EntitlementValue.Integer(5))],
            lease.Entitlements);
        Assert.Equal(new long[] { 1_785_902_400, 1_793_073_600, 1_794_283_200 }, new[] { lease.RefreshAfter, lease.ExpiresAt, lease.GraceUntil });
    }

    [Fact]
    public void TypedCustomEntitlements()
    {
        var lease = Verify("paid_lease_custom_entitlements_valid_p256_es256", LeaseType.Paid);
        Assert.Equal(["app.major_family", "activation.limit", "qa.option4.boolean", "qa.option4.datetime", "qa.option4.integer", "qa.option4.string"],
            lease.Entitlements.Select(e => e.Code));
        Assert.Equal(new EntitlementValue.DateTime(Timestamp.Parse("2027-08-04T00:00:00.000Z")!.Value), lease.Entitlements[3].Value);
        Assert.Equal(new EntitlementValue.Text("studio"), lease.Entitlements[5].Value);
    }

    [Fact]
    public void TrialLease()
    {
        var lease = Verify("trial_lease_valid_p256_es256", LeaseType.Trial);
        Assert.Equal(new LeaseKind.Trial("trl_golden_trial_001", Timestamp.Parse("2026-08-30T12:00:00.000Z")!.Value, 1), lease.Kind);
        Assert.Contains(new Entitlement("trial", new EntitlementValue.Boolean(true)), lease.Entitlements);
    }

    [Fact]
    public void PaidAndTrialVerifiersRefuseEachOther()
    {
        var paid = Assert.Throws<VerificationException>(() => Verify("trial_lease_valid_p256_es256", LeaseType.Paid));
        Assert.Equal((VerificationFailure.UnexpectedHeader, "typ"), (paid.Failure, paid.Detail));
        var trial = Assert.Throws<VerificationException>(() => Verify("paid_lease_valid_p256_es256", LeaseType.Trial));
        Assert.Equal((VerificationFailure.UnexpectedHeader, "typ"), (trial.Failure, trial.Detail));
    }

    [Fact]
    public void AnotherTenantsLeaseIsRefused()
    {
        var error = Assert.Throws<VerificationException>(() => Verify("paid_lease_valid_p256_es256", LeaseType.Paid, "tenant_other"));
        Assert.Equal((VerificationFailure.WrongScope, "lease tenant"), (error.Failure, error.Detail));
    }
}
