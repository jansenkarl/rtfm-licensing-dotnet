namespace RTFM.Licensing;

/// <summary>What the app should do now: run licensed, run in trial, or ask for a key.</summary>
public abstract record LicenseStatus
{
    private LicenseStatus() { }

    /// <summary>Whether the app may be used: licensed or in a trial.</summary>
    public bool AllowsUse => this is Licensed or Trial;

    /// <summary>Not yet checked (before <see cref="RTFMLicense.StartAsync"/> finishes).</summary>
    public sealed record Checking : LicenseStatus;

    /// <summary>A key is active.</summary>
    public sealed record Licensed(LicenseInfo Info) : LicenseStatus;

    /// <summary>A trial is running.</summary>
    public sealed record Trial(TrialInfo Info) : LicenseStatus;

    /// <summary>Ask for a key; <see cref="Reason"/> says why.</summary>
    public sealed record NotLicensed(NotLicensedReason Reason) : LicenseStatus;
}

/// <summary>An active key.</summary>
public sealed record LicenseInfo
{
    /// <summary>The license's ID.</summary>
    public required string LicenseId { get; init; }

    /// <summary>The signed entitlements, including custom license fields.</summary>
    public required IReadOnlyList<Entitlement> Entitlements { get; init; }

    /// <summary>For a fixed-term or subscription key: show "Renews …" or "Ends …" with <see cref="Licensing.Term.EndsAt"/>.</summary>
    public Term? Term { get; init; }

    /// <summary>The lease expired and the licensing service could not be reached; use continues until <see cref="GraceUntil"/>.</summary>
    public required bool InGrace { get; init; }

    /// <summary>When grace ends.</summary>
    public required DateTimeOffset GraceUntil { get; init; }

    /// <summary>When the current lease was issued: the last time the licensing service confirmed this key.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>The value of the entitlement <paramref name="code"/>, or null.</summary>
    public EntitlementValue? Entitlement(string code) => Entitlements.FirstOrDefault(e => e.Code == code)?.Value;
}

/// <summary>A running trial.</summary>
/// <param name="EndsAt">When the trial ends.</param>
/// <param name="InGrace">The lease expired offline; use continues within grace.</param>
/// <param name="Entitlements">The trial's signed values.</param>
public sealed record TrialInfo(DateTimeOffset EndsAt, bool InGrace, IReadOnlyList<Entitlement> Entitlements);

/// <summary>Why the app is not licensed.</summary>
public enum NotLicensedKind
{
    /// <summary>No key has been entered on this computer (and no trial started).</summary>
    NotActivated,
    /// <summary>The lease expired and could not be renewed; going online renews it.</summary>
    Expired,
    /// <summary>The fixed term or subscription has ended.</summary>
    TermEnded,
    /// <summary>The trial has ended.</summary>
    TrialEnded,
    /// <summary>The key was revoked, deactivated elsewhere, or otherwise refused by the licensing service.</summary>
    Refused,
    /// <summary>The computer's clock is earlier than a time already seen; correct the date and time.</summary>
    ClockRolledBack,
    /// <summary>The stored license did not verify; activate again.</summary>
    InvalidStoredLicense,
}

/// <summary>Why the app is not licensed, with the service's code for <see cref="NotLicensedKind.Refused"/>.</summary>
public sealed record NotLicensedReason(NotLicensedKind Kind, string? Code = null);

/// <summary>Turns stored state into a status, offline: verifies the stored leases again and applies their timing.</summary>
internal static class StatusEvaluator
{
    /// <summary>A clock may move back a little (time sync); only a larger step back is treated as a rollback.</summary>
    public const long ClockTolerance = 300;

    public static LicenseStatus Evaluate(LicenseState state, AppIntegration integration, long now, NetworkState network)
    {
        static LicenseStatus Not(NotLicensedKind kind) => new LicenseStatus.NotLicensed(new NotLicensedReason(kind));
        VerificationManifest manifest;
        string thumbprint;
        try
        {
            manifest = new VerificationManifest(state.Manifest ?? throw VerificationException.Malformed("no manifest"), integration);
            using var identity = InstallationIdentity.Open(state.InstallationKey);
            thumbprint = identity.Thumbprint;
        }
        catch (Exception error) when (error is VerificationException or System.Security.Cryptography.CryptographicException)
        {
            return Not(state.PaidLease is null && state.TrialLease is null ? NotLicensedKind.NotActivated : NotLicensedKind.InvalidStoredLicense);
        }
        // The stored leases were accepted at or above today's high-water marks, so they are checked from zero here.
        var expectations = new LeaseExpectations(integration, thumbprint, new HighWater());
        var observed = state.HighWater.ObservedTime - ClockTolerance;
        NotLicensedKind? paidReason = null;
        if (state.PaidLease is { } paidJws)
        {
            Lease lease;
            try { lease = LeaseVerifier.Verify(paidJws, LeaseType.Paid, manifest, expectations); }
            catch (VerificationException) { return Not(NotLicensedKind.InvalidStoredLicense); }
            var paid = (LeaseKind.Paid)lease.Kind;
            if (lease.Term is { } term && now >= term.AccessEndsAt.ToUnixTimeSeconds()) paidReason = NotLicensedKind.TermEnded;
            else
            {
                switch (AccessTiming.Decide(lease, now, observed, network))
                {
                    case AccessDecision.Valid or AccessDecision.Grace:
                        return new LicenseStatus.Licensed(new LicenseInfo
                        {
                            LicenseId = paid.LicenseId, Entitlements = lease.Entitlements, Term = lease.Term, InGrace = now > lease.ExpiresAt,
                            GraceUntil = DateTimeOffset.FromUnixTimeSeconds(lease.GraceUntil), IssuedAt = DateTimeOffset.FromUnixTimeSeconds(lease.IssuedAt),
                        });
                    case AccessDecision.ClockRolledBack or AccessDecision.NotYetValid:
                        return Not(NotLicensedKind.ClockRolledBack);
                    default:
                        paidReason = NotLicensedKind.Expired;
                        break;
                }
            }
        }
        if (state.TrialLease is { } trialJws)
        {
            Lease lease;
            try { lease = LeaseVerifier.Verify(trialJws, LeaseType.Trial, manifest, expectations); }
            catch (VerificationException) { return Not(paidReason ?? NotLicensedKind.InvalidStoredLicense); }
            var trial = (LeaseKind.Trial)lease.Kind;
            return AccessTiming.Decide(lease, now, observed, network) switch
            {
                AccessDecision.Valid or AccessDecision.Grace => new LicenseStatus.Trial(new TrialInfo(trial.EndsAt, now > lease.ExpiresAt, lease.Entitlements)),
                AccessDecision.ClockRolledBack or AccessDecision.NotYetValid => Not(paidReason ?? NotLicensedKind.ClockRolledBack),
                AccessDecision.TrialEnded => Not(paidReason ?? NotLicensedKind.TrialEnded),
                _ => Not(paidReason ?? NotLicensedKind.Expired),
            };
        }
        return Not(paidReason ?? NotLicensedKind.NotActivated);
    }
}
