using System.Text.Json;
using System.Text.Json.Nodes;

namespace RTFM.Licensing;

/// <summary>The online side of licensing: challenges, activation, refresh, deactivation and trials. Each request is
/// written to the store before it is sent and replayed exactly after a lost response, so an activation is never spent
/// twice; its result and the raised high-water marks are committed together. One operation runs at a time.</summary>
public sealed partial class LicensingClient : IDisposable
{
    private readonly LicensingApi api;
    private readonly ILicenseStore store;
    private readonly Func<DateTimeOffset> now;
    private readonly bool preferTpm;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    /// <summary>The state as last read or committed, so the store is read once per launch.</summary>
    private LicenseState? cached;

    /// <summary>A client for <paramref name="integration"/>. Pass <paramref name="http"/> to use your own HttpClient
    /// (or a test double); <paramref name="now"/> for a test clock.</summary>
    public LicensingClient(AppIntegration integration, ILicenseStore store, string appVersion, HttpClient? http = null,
        Func<DateTimeOffset>? now = null, bool preferTpm = true)
    {
        ownsHttp = http is null;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        api = new LicensingApi(integration, this.http, appVersion);
        this.store = store;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.preferTpm = preferTpm;
    }

    /// <summary>The App integration file this client uses.</summary>
    public AppIntegration Integration => api.Integration;

    internal long UnixNow => now().ToUnixTimeSeconds();

    // State

    /// <summary>The stored state, creating the installation key on first use.</summary>
    internal LicenseState State()
    {
        if (cached is not null) return cached;
        LicenseState? stored;
        try { stored = store.Load(); }
        catch (StoreException error) { throw LicensingException.Storage(error.Message, error); }
        if (stored is not null) return cached = stored;
        using var identity = InstallationIdentity.Generate(preferTpm);
        var fresh = new LicenseState { InstallationKey = identity.Reference };
        Commit(fresh);
        return fresh;
    }

    internal void Commit(LicenseState state)
    {
        try { store.Commit(state); }
        catch (Exception error) { throw LicensingException.Storage(error.Message, error); }
        cached = state;
    }

    private static InstallationIdentity Identity(LicenseState state)
    {
        try { return InstallationIdentity.Open(state.InstallationKey); }
        catch (System.Security.Cryptography.CryptographicException error) { throw LicensingException.Storage("installation key unreadable", error); }
    }

    private async Task<T> Exclusive<T>(Func<Task<T>> work)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Paid operations

    /// <summary>Activates <paramref name="productKey"/> on this computer. <paramref name="deviceLabel"/> is an optional
    /// name shown to you and the customer (for example the computer's name), sent only if you pass one.</summary>
    public Task<Lease> ActivateAsync(string productKey, string? deviceLabel = null, CancellationToken cancel = default) => Exclusive(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        var state = State();
        using var identity = Identity(state);
        var challenge = await api.ChallengeAsync(identity, null, null, cancel).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["product_id"] = Integration.ProductCode, ["release_family_id"] = Integration.ProductVersionCode,
            ["product_key"] = productKey.Trim(), ["device_public_key"] = LicensingApi.DevicePublicKey(identity),
            ["challenge"] = PaidProof(challenge, identity),
        };
        if (!string.IsNullOrEmpty(deviceLabel)) body["device_label"] = deviceLabel;
        return (await PerformAsync(OperationKind.Activate, "/v1/activations", LicensingApi.Encode(body), state, cancel).ConfigureAwait(false))!;
    });

    /// <summary>Renews the paid lease now.</summary>
    public Task<Lease> RefreshAsync(CancellationToken cancel = default) => Exclusive(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        var state = State();
        var activation = state.Activation ?? throw new LicensingException(LicensingFailure.NotActivated, "This computer has no activation.");
        var body = await CredentialBody(activation.RefreshCredential, state, cancel).ConfigureAwait(false);
        return (await PerformAsync(OperationKind.Refresh, $"/v1/activations/{activation.ActivationId}/leases", body, state, cancel).ConfigureAwait(false))!;
    });

    /// <summary>Releases this computer's activation so the key can be used on another.</summary>
    public Task DeactivateAsync(CancellationToken cancel = default) => Exclusive<object?>(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        var state = State();
        var activation = state.Activation ?? throw new LicensingException(LicensingFailure.NotActivated, "This computer has no activation.");
        var body = await CredentialBody(activation.RefreshCredential, state, cancel).ConfigureAwait(false);
        await PerformAsync(OperationKind.Deactivate, $"/v1/activations/{activation.ActivationId}/deactivate", body, state, cancel).ConfigureAwait(false);
        return null;
    });

    private async Task<byte[]> CredentialBody(string credential, LicenseState state, CancellationToken cancel)
    {
        using var identity = Identity(state);
        var challenge = await api.ChallengeAsync(identity, null, null, cancel).ConfigureAwait(false);
        return LicensingApi.Encode(new JsonObject
        {
            ["refresh_credential"] = credential, ["device_public_key"] = LicensingApi.DevicePublicKey(identity), ["challenge"] = PaidProof(challenge, identity),
        });
    }

    private static JsonObject PaidProof((string Id, string Nonce) challenge, InstallationIdentity identity) => new()
    {
        ["id"] = challenge.Id, ["nonce"] = challenge.Nonce, ["proof_version"] = "rtfm-device-challenge-v1",
        ["signature"] = identity.Sign($"rtfm-device-challenge-v1:{challenge.Id}:{challenge.Nonce}"),
    };

    // Trials

    /// <summary>Starts a trial, or resumes this computer's trial if it already has one.</summary>
    public Task<Lease> StartTrialAsync(CancellationToken cancel = default) => Exclusive(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        var state = State();
        var body = await TrialBody("trial_start", null, new JsonObject
        {
            ["product_id"] = Integration.ProductCode, ["release_family_id"] = Integration.ProductVersionCode,
        }, state, cancel).ConfigureAwait(false);
        return (await PerformAsync(OperationKind.StartTrial, "/v1/trials", body, state, cancel).ConfigureAwait(false))!;
    });

    /// <summary>Renews the trial lease now.</summary>
    public Task<Lease> RefreshTrialAsync(CancellationToken cancel = default) => Exclusive(async () =>
    {
        await CompletePendingCore(cancel).ConfigureAwait(false);
        var state = State();
        if (state.TrialId is not { } trialId || state.TrialRefreshCredential is not { } credential)
        {
            throw new LicensingException(LicensingFailure.NoTrial, "This computer has no trial.");
        }
        var body = await TrialBody("trial_refresh", trialId, new JsonObject { ["refresh_credential"] = credential }, state, cancel).ConfigureAwait(false);
        return (await PerformAsync(OperationKind.RefreshTrial, $"/v1/trials/{trialId}/leases", body, state, cancel).ConfigureAwait(false))!;
    });

    private async Task<byte[]> TrialBody(string purpose, string? subjectId, JsonObject body, LicenseState state, CancellationToken cancel)
    {
        using var identity = Identity(state);
        var challenge = await api.ChallengeAsync(identity, purpose, subjectId, cancel).ConfigureAwait(false);
        body["device_public_key"] = LicensingApi.DevicePublicKey(identity);
        body["client"] = api.ClientMetadata();
        body["challenge"] = new JsonObject
        {
            ["id"] = challenge.Id, ["nonce"] = challenge.Nonce,
            ["signature"] = identity.Sign($"rtfm-device-challenge-v2:{challenge.Id}:{challenge.Nonce}:{purpose}:{subjectId ?? "--"}"),
        };
        return LicensingApi.Encode(body);
    }

    // Used by RTFMLicense

    /// <summary>Forgets the paid activation (and its lease) or the trial after the service refused it for good.</summary>
    internal void Forget(bool activation, bool trial)
    {
        var next = State();
        if (activation) next = next with { Activation = null, PaidLease = null };
        if (trial) next = next with { TrialLease = null, TrialId = null, TrialRefreshCredential = null };
        Commit(next);
    }

    /// <summary>The stored lease's quiet-refresh time (it was verified when accepted).</summary>
    internal long? RefreshAfter(LeaseType kind)
    {
        var state = State();
        var jws = kind == LeaseType.Paid ? state.PaidLease : state.TrialLease;
        return jws is null ? null : CompactJws.UnverifiedPayload(jws)?.Property("refresh_after").SafeInteger();
    }

    internal LicenseStatus CurrentStatus(NetworkState network) => StatusEvaluator.Evaluate(State(), Integration, UnixNow, network);

    internal void RecordObservedTime() => Commit(State().Observing(UnixNow));

    /// <inheritdoc />
    public void Dispose()
    {
        gate.Dispose();
        if (ownsHttp) http.Dispose();
    }
}
