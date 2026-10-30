using System.Text.Json;

namespace RTFM.Licensing;

public sealed partial class LicensingClient
{
    /// <summary>Writes the request down, sends it, and commits its verified result.</summary>
    private async Task<Lease?> PerformAsync(OperationKind operation, string path, byte[] body, LicenseState state, CancellationToken cancel)
    {
        var journaled = state with { Pending = new PendingOperation(operation, path, body, LicensingApi.NewIdempotencyKey()) };
        Commit(journaled);
        return await SendPendingAsync(journaled.Pending, journaled, cancel).ConfigureAwait(false);
    }

    /// <summary>Replays a request left pending by a lost response or a failed commit, before anything new is sent.</summary>
    public Task CompletePendingAsync(CancellationToken cancel = default) => Exclusive<object?>(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        return null;
    });

    private async Task CompletePendingCore(CancellationToken cancel)
    {
        var state = State();
        if (state.Pending is { } pending) await SendPendingAsync(pending, state, cancel).ConfigureAwait(false);
    }

    /// <summary>A network or retryable service failure keeps the request pending for an exact replay; a refusal clears
    /// it. A success is verified, then committed with the pending request cleared.</summary>
    private async Task<Lease?> SendPendingAsync(PendingOperation pending, LicenseState state, CancellationToken cancel)
    {
        JsonElement response;
        try
        {
            response = await api.SendAsync(HttpMethod.Post, pending.Path, pending.Body, pending.IdempotencyKey, cancel).ConfigureAwait(false);
        }
        catch (LicensingException error) when (error.Failure == LicensingFailure.Refused)
        {
            Commit(state with { Pending = null });
            throw;
        }
        try
        {
            return await CommitResultAsync(response, pending.Operation, state, cancel).ConfigureAwait(false);
        }
        catch (LicensingException error) when (error.Verification is { } verification)
        {
            // A lease naming a key not yet listed may verify after the manifest is fetched again later, so the request
            // stays pending; any other verification failure will not change on replay.
            if (verification.Failure != VerificationFailure.UnknownKey) Commit(state with { Pending = null });
            throw;
        }
    }

    private async Task<Lease?> CommitResultAsync(JsonElement response, OperationKind operation, LicenseState state, CancellationToken cancel)
    {
        var next = state with { Pending = null };
        Lease? lease = null;
        switch (operation)
        {
            case OperationKind.Activate:
                if (response.Property("activation_id").StringValue() is not { } id || response.Property("refresh_credential").StringValue() is not { } credential)
                {
                    throw LicensingException.Service("invalid_response");
                }
                next = next with { Activation = new ActivationRecord(id, credential) };
                (lease, next) = await VerifyAsync(response.Property("lease"), LeaseType.Paid, next, cancel).ConfigureAwait(false);
                break;
            case OperationKind.Refresh:
                (lease, next) = await VerifyAsync(response.Property("lease"), LeaseType.Paid, next, cancel).ConfigureAwait(false);
                break;
            case OperationKind.Deactivate:
                next = next with { Activation = null, PaidLease = null };
                break;
            case OperationKind.StartTrial or OperationKind.RefreshTrial:
                if (response.Property("trial_id").StringValue() is not { } trialId || response.Property("refresh_credential").StringValue() is not { } trialCredential)
                {
                    throw LicensingException.Service("invalid_response");
                }
                next = next with { TrialId = trialId, TrialRefreshCredential = trialCredential };
                (lease, next) = await VerifyAsync(response.Property("lease"), LeaseType.Trial, next, cancel).ConfigureAwait(false);
                break;
        }
        Commit(next.Observing(UnixNow));
        return lease;
    }

    /// <summary>Verifies a lease from a response against the held manifest (fetching it, or once more for a new
    /// <c>kid</c>), and adds it and its high-water marks to the state.</summary>
    private async Task<(Lease, LicenseState)> VerifyAsync(JsonElement? value, LeaseType kind, LicenseState state, CancellationToken cancel)
    {
        var jws = value.StringValue() ?? throw LicensingException.Service("invalid_response");
        using var identity = Identity(state);
        var (manifest, withManifest) = await ManifestForAsync(jws, state, cancel).ConfigureAwait(false);
        try
        {
            var lease = LeaseVerifier.Verify(jws, kind, manifest, new LeaseExpectations(Integration, identity.Thumbprint, withManifest.HighWater));
            return (lease, withManifest.Accepting(lease));
        }
        catch (VerificationException error) { throw LicensingException.Verify(error); }
    }

    /// <summary>The manifest that should list the lease's key, following the key-rotation rule.</summary>
    private async Task<(VerificationManifest, LicenseState)> ManifestForAsync(string jws, LicenseState state, CancellationToken cancel)
    {
        string kid;
        try { kid = CompactJws.UnverifiedKeyId(jws); }
        catch (VerificationException error) { throw LicensingException.Verify(error); }
        VerificationManifest held;
        try { held = new VerificationManifest(state.Manifest ?? throw VerificationException.Malformed("no manifest"), Integration); }
        catch (VerificationException)
        {
            var fetched = await FetchManifestAsync(cancel).ConfigureAwait(false);
            return (fetched, state with { Manifest = fetched.Jws });
        }
        DateTimeOffset? last = state.ManifestRefetches.TryGetValue(kid, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        var outcome = await KeyRotation.ResolveAsync(kid, held, last, now(), () => FetchManifestAsync(cancel)).ConfigureAwait(false);
        var refetches = state.ManifestRefetches;
        if (outcome.Fetched) refetches = new Dictionary<string, long>(refetches) { [kid] = UnixNow };
        return (outcome.Manifest, state with { Manifest = outcome.Manifest.Jws, ManifestRefetches = refetches });
    }

    private async Task<VerificationManifest> FetchManifestAsync(CancellationToken cancel)
    {
        var jws = await api.VerificationManifestJwsAsync(cancel).ConfigureAwait(false);
        try { return new VerificationManifest(jws, Integration); }
        catch (VerificationException error) { throw LicensingException.Verify(error); }
    }
}
