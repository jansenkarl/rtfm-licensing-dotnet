using System.Text.Json;
using System.Text.Json.Serialization;

namespace RTFM.Licensing;

/// <summary>Everything the SDK keeps between launches. It is stored as one item, so each commit replaces it whole: a
/// verified response and the high-water marks it raises are saved together or not at all.</summary>
public sealed record LicenseState
{
    /// <summary>Where the installation's private key is: in the TPM (by name) or here (PKCS#8, protected with the
    /// state).</summary>
    [JsonPropertyName("installationKey")] public required InstallationKeyReference InstallationKey { get; init; }

    /// <summary>The paid activation, if any.</summary>
    [JsonPropertyName("activation")] public ActivationRecord? Activation { get; init; }

    /// <summary>The latest accepted paid lease (JWS); verified again on every launch.</summary>
    [JsonPropertyName("paidLease")] public string? PaidLease { get; init; }

    /// <summary>The latest accepted trial lease (JWS).</summary>
    [JsonPropertyName("trialLease")] public string? TrialLease { get; init; }

    /// <summary>The trial's ID.</summary>
    [JsonPropertyName("trialID")] public string? TrialId { get; init; }

    /// <summary>The trial's refresh credential.</summary>
    [JsonPropertyName("trialRefreshCredential")] public string? TrialRefreshCredential { get; init; }

    /// <summary>The newest root-signed manifest held (JWS).</summary>
    [JsonPropertyName("manifest")] public string? Manifest { get; init; }

    /// <summary>The marks already accepted.</summary>
    [JsonPropertyName("highWater")] public HighWater HighWater { get; init; } = new();

    /// <summary>When the manifest was last fetched again for each unlisted <c>kid</c> (Unix seconds).</summary>
    [JsonPropertyName("manifestRefetches")] public IReadOnlyDictionary<string, long> ManifestRefetches { get; init; } = new Dictionary<string, long>();

    /// <summary>A request written down before it was sent, replayed exactly until its response is committed.</summary>
    [JsonPropertyName("pendingOperation")] public PendingOperation? Pending { get; init; }

    internal LicenseState Accepting(Lease lease)
    {
        var marks = HighWater;
        switch (lease.Kind)
        {
            case LeaseKind.Paid paid:
                return this with
                {
                    PaidLease = lease.Jws,
                    HighWater = marks with
                    {
                        Sequence = marks.ActivationId == paid.ActivationId ? Math.Max(marks.Sequence, lease.Sequence) : lease.Sequence,
                        RevocationEpoch = marks.LicenseId == paid.LicenseId ? Math.Max(marks.RevocationEpoch, paid.RevocationEpoch) : paid.RevocationEpoch,
                        ActivationId = paid.ActivationId, LicenseId = paid.LicenseId,
                    },
                };
            case LeaseKind.Trial trial:
                var same = marks.TrialId == trial.TrialId;
                return this with
                {
                    TrialLease = lease.Jws, TrialId = trial.TrialId,
                    HighWater = marks with
                    {
                        TrialSequence = same ? Math.Max(marks.TrialSequence ?? 0, lease.Sequence) : lease.Sequence,
                        TrialRevision = same ? Math.Max(marks.TrialRevision, trial.Revision) : trial.Revision,
                        TrialId = trial.TrialId,
                    },
                };
            default:
                throw new InvalidOperationException("Unknown lease kind.");
        }
    }

    /// <summary>Records the latest wall-clock time seen, so a clock set back later is noticed.</summary>
    internal LicenseState Observing(long now) => this with { HighWater = HighWater with { ObservedTime = Math.Max(HighWater.ObservedTime, now) } };
}

/// <summary>Where the installation key is kept.</summary>
/// <param name="Storage"><c>tpm</c> (a non-exportable key in the TPM) or <c>software</c>.</param>
/// <param name="Pkcs8">The software key (PKCS#8), protected with the rest of the state.</param>
/// <param name="KeyName">The TPM key's name in Windows key storage.</param>
public sealed record InstallationKeyReference(
    [property: JsonPropertyName("storage")] string Storage,
    [property: JsonPropertyName("pkcs8")] byte[]? Pkcs8 = null,
    [property: JsonPropertyName("keyName")] string? KeyName = null);

/// <summary>A paid activation.</summary>
public sealed record ActivationRecord(
    [property: JsonPropertyName("activationID")] string ActivationId,
    [property: JsonPropertyName("refreshCredential")] string RefreshCredential);

/// <summary>Which online operation a pending request is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperationKind>))]
public enum OperationKind
{
    /// <summary>Activate a key.</summary>
    Activate,
    /// <summary>Renew a paid lease.</summary>
    Refresh,
    /// <summary>Release this installation's activation.</summary>
    Deactivate,
    /// <summary>Start a trial.</summary>
    StartTrial,
    /// <summary>Renew a trial lease.</summary>
    RefreshTrial,
}

/// <summary>Endpoint, exact body and idempotency key of a request that may already have reached the server.</summary>
public sealed record PendingOperation(
    [property: JsonPropertyName("operation")] OperationKind Operation,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("body")] byte[] Body,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey);

/// <summary>Turns <see cref="LicenseState"/> into bytes and back.</summary>
internal static class LicenseStateCoding
{
    public static byte[] Encode(LicenseState state) => JsonSerializer.SerializeToUtf8Bytes(state);

    public static LicenseState Decode(byte[] data)
    {
        try { return JsonSerializer.Deserialize<LicenseState>(data) ?? throw new StoreException("The stored license state is empty."); }
        catch (JsonException error) { throw new StoreException("The stored license state could not be read.", error); }
    }
}
