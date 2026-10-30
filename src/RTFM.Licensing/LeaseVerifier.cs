using System.Text.Json;

namespace RTFM.Licensing;

/// <summary>Paid or trial: a paid verifier refuses trial leases and the reverse.</summary>
public enum LeaseType
{
    /// <summary>A paid key's lease.</summary>
    Paid,
    /// <summary>A trial's lease.</summary>
    Trial,
}

/// <summary>Verifies paid and trial leases against a verified manifest. Timing (expiry and grace) is decided
/// separately by <see cref="AccessTiming"/>.</summary>
public static class LeaseVerifier
{
    /// <summary>Verifies <paramref name="jws"/> and returns the lease, or throws <see cref="VerificationException"/>.</summary>
    public static Lease Verify(string jws, LeaseType expected, VerificationManifest manifest, LeaseExpectations expectations)
    {
        var kid = CompactJws.UnverifiedKeyId(jws);
        var key = manifest.ListedKey(kid) ?? throw new VerificationException(VerificationFailure.UnknownKey, kid);
        if (key.Status != "active") throw new VerificationException(VerificationFailure.InactiveKey, kid);
        var type = expected == LeaseType.Paid ? CompactJws.LeaseType : CompactJws.TrialLeaseType;
        var claims = CompactJws.Verify(jws, key.PublicKey, kid, type);
        return FromClaims(claims, jws, kid, expected, expectations);
    }

    internal static Lease FromClaims(JsonElement claims, string jws, string kid, LeaseType expected, LeaseExpectations expectations)
    {
        var integration = expectations.Integration;
        var schema = expected == LeaseType.Paid ? "rtfm.license-lease.v1" : "rtfm.trial-lease.v1";
        if (!claims.Property("schema").IsString(schema)) throw VerificationException.Malformed("lease schema");
        if (!claims.Property("kid").IsString(kid)) throw VerificationException.WrongScope("lease kid");
        if (!claims.Property("iss").IsString(integration.Issuer)) throw VerificationException.WrongScope("lease issuer");
        if (claims.Property("tenant_id").StringValue() is not { Length: > 0 } tenant || (integration.TenantId is not null && integration.TenantId != tenant))
        {
            throw VerificationException.WrongScope("lease tenant");
        }
        if (!claims.Property("product_id").IsString(integration.ProductCode)) throw VerificationException.WrongScope("lease product");
        if (!claims.Property("release_family_id").IsString(integration.ProductVersionCode)) throw VerificationException.WrongScope("lease product version");
        if (!claims.Property("device_thumbprint").IsString(expectations.DeviceThumbprint)) throw VerificationException.WrongScope("lease installation");
        long Integer(string name) => claims.Property(name).SafeInteger() ?? throw VerificationException.Malformed($"lease {name}");
        string Text(string name) => claims.Property(name).StringValue() is { Length: > 0 } value ? value : throw VerificationException.Malformed($"lease {name}");

        var sequence = Integer("sequence");
        long issuedAt = Integer("iat"), notBefore = Integer("nbf");
        long refreshAfter = Integer("refresh_after"), expiresAt = Integer("exp"), graceUntil = Integer("grace_until");
        if (!(refreshAfter <= expiresAt && expiresAt <= graceUntil)) throw VerificationException.Malformed("lease timing order");
        var highWater = expectations.HighWater;
        LeaseKind kind;
        bool typed;
        if (expected == LeaseType.Paid)
        {
            var epoch = Integer("revocation_epoch");
            string licenseId = Text("license_id"), activationId = Text("activation_id");
            if (HighWater.Applies(highWater.ActivationId, activationId) && sequence < highWater.Sequence)
            {
                throw new VerificationException(VerificationFailure.StaleState, "lease sequence");
            }
            if (HighWater.Applies(highWater.LicenseId, licenseId) && epoch < highWater.RevocationEpoch)
            {
                throw new VerificationException(VerificationFailure.StaleState, "revocation epoch");
            }
            kind = new LeaseKind.Paid(licenseId, activationId, epoch);
            typed = claims.Property("entitlement_revision") is { } revision
                && (((JsonElement?)revision).SafeInteger() is not null ? true : throw VerificationException.Malformed("lease entitlement_revision"));
        }
        else
        {
            if (!claims.Property("lease_kind").IsString("trial")) throw VerificationException.Malformed("trial lease kind");
            var revision = Integer("trial_revision");
            var trialId = Text("trial_id");
            if (HighWater.Applies(highWater.TrialId, trialId))
            {
                if (sequence < (highWater.TrialSequence ?? 0)) throw new VerificationException(VerificationFailure.StaleState, "lease sequence");
                if (revision < highWater.TrialRevision) throw new VerificationException(VerificationFailure.StaleState, "trial revision");
            }
            var endsAt = Timestamp.Parse(Text("trial_ends_at")) ?? throw VerificationException.Malformed("trial_ends_at");
            if (graceUntil > endsAt.ToUnixTimeSeconds()) throw VerificationException.Malformed("trial lease grace exceeds the trial end");
            kind = new LeaseKind.Trial(trialId, endsAt, revision);
            typed = false;
        }
        return new Lease
        {
            Kind = kind, Jws = jws, KeyId = kid, Sequence = sequence, IssuedAt = issuedAt, NotBefore = notBefore,
            RefreshAfter = refreshAfter, ExpiresAt = expiresAt, GraceUntil = graceUntil,
            Entitlements = Entitlement.ParseList(claims.Property("entitlements"), typed), Term = ParseTerm(claims.Property("term")),
        };
    }

    /// <summary>The optional <c>term</c> claim of a fixed-term or subscription key.</summary>
    internal static Term? ParseTerm(JsonElement? value)
    {
        if (value is null) return null;
        TermKind? kind = value.Value.Property("type").StringValue() switch { "fixed" => TermKind.Fixed, "subscription" => TermKind.Subscription, _ => null };
        var endsAt = Timestamp.Parse(value.Value.Property("ends_at").StringValue());
        var accessEndsAt = Timestamp.Parse(value.Value.Property("access_ends_at").StringValue());
        if (value.Value.ValueKind != JsonValueKind.Object || kind is null || endsAt is null || accessEndsAt is null || accessEndsAt < endsAt)
        {
            throw VerificationException.Malformed("lease term");
        }
        return new Term(kind.Value, endsAt.Value, accessEndsAt.Value);
    }
}
