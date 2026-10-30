using System.Text.Json.Serialization;

namespace RTFM.Licensing;

/// <summary>What a lease is for.</summary>
public abstract record LeaseKind
{
    private LeaseKind() { }

    /// <summary>A paid key's lease for one activation.</summary>
    public sealed record Paid(string LicenseId, string ActivationId, long RevocationEpoch) : LeaseKind;

    /// <summary>A trial's lease.</summary>
    public sealed record Trial(string TrialId, DateTimeOffset EndsAt, long Revision) : LeaseKind;
}

/// <summary>The kind of a key's term.</summary>
public enum TermKind
{
    /// <summary>Ends on a date.</summary>
    Fixed,
    /// <summary>Renews with a store subscription.</summary>
    Subscription,
}

/// <summary>The term of a fixed-term or subscription key.</summary>
/// <param name="Kind">Fixed or subscription.</param>
/// <param name="EndsAt">The term's end (for a subscription, its renewal date).</param>
/// <param name="AccessEndsAt">When the key stops working: the end plus the Product Version's grace days.</param>
public sealed record Term(TermKind Kind, DateTimeOffset EndsAt, DateTimeOffset AccessEndsAt);

/// <summary>A lease whose signature, scope and high-water marks have been checked.</summary>
public sealed record Lease
{
    /// <summary>Paid or trial, with its IDs.</summary>
    public required LeaseKind Kind { get; init; }

    /// <summary>The signed lease.</summary>
    public required string Jws { get; init; }

    /// <summary>The ID of the key that signed it.</summary>
    public required string KeyId { get; init; }

    /// <summary>The lease's number within its activation or trial.</summary>
    public required long Sequence { get; init; }

    /// <summary>Unix seconds: when it was issued.</summary>
    public required long IssuedAt { get; init; }

    /// <summary>Unix seconds: valid from.</summary>
    public required long NotBefore { get; init; }

    /// <summary>Unix seconds: refresh quietly after this; it is never a blocking point.</summary>
    public required long RefreshAfter { get; init; }

    /// <summary>Unix seconds: the lease is valid until this.</summary>
    public required long ExpiresAt { get; init; }

    /// <summary>Unix seconds: after <see cref="ExpiresAt"/>, a network or service failure still allows use until this.</summary>
    public required long GraceUntil { get; init; }

    /// <summary>The signed entitlements, including custom license fields.</summary>
    public required IReadOnlyList<Entitlement> Entitlements { get; init; }

    /// <summary>The term of a fixed-term or subscription key; null for a perpetual key.</summary>
    public Term? Term { get; init; }
}

/// <summary>The highest values already accepted; a lease below them is a replay or a rollback and is refused.</summary>
/// <remarks>The server numbers each activation's leases, each license's revocation epoch and each trial's leases
/// separately, so each mark applies to the activation, license or trial it was seen for. A mark with no recorded scope
/// applies to every lease of its kind.</remarks>
public sealed record HighWater
{
    /// <summary>Paid lease sequence, for <see cref="ActivationId"/>.</summary>
    [JsonPropertyName("sequence")] public long Sequence { get; init; }

    /// <summary>Revocation epoch, for <see cref="LicenseId"/>.</summary>
    [JsonPropertyName("revocationEpoch")] public long RevocationEpoch { get; init; }

    /// <summary>Trial lease revision, for <see cref="TrialId"/>.</summary>
    [JsonPropertyName("trialRevision")] public long TrialRevision { get; init; }

    /// <summary>Trial lease sequence, for <see cref="TrialId"/>.</summary>
    [JsonPropertyName("trialSequence")] public long? TrialSequence { get; init; }

    /// <summary>The latest wall-clock time the app has observed, in Unix seconds.</summary>
    [JsonPropertyName("observedTime")] public long ObservedTime { get; init; }

    /// <summary>The activation the sequence mark belongs to.</summary>
    [JsonPropertyName("activationID")] public string? ActivationId { get; init; }

    /// <summary>The license the revocation epoch belongs to.</summary>
    [JsonPropertyName("licenseID")] public string? LicenseId { get; init; }

    /// <summary>The trial the trial marks belong to.</summary>
    [JsonPropertyName("trialID")] public string? TrialId { get; init; }

    /// <summary>Whether a mark recorded for <paramref name="recorded"/> applies to a lease for <paramref name="id"/>.</summary>
    internal static bool Applies(string? recorded, string id) => recorded is null || recorded == id;
}

/// <summary>What a lease must match: the app's own Product Version and installation, and what the app has already
/// seen.</summary>
/// <param name="Integration">The App integration file.</param>
/// <param name="DeviceThumbprint">This installation's key thumbprint.</param>
/// <param name="HighWater">The marks already accepted.</param>
public sealed record LeaseExpectations(AppIntegration Integration, string DeviceThumbprint, HighWater HighWater);
