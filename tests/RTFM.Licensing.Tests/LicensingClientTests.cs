using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

public class LicensingClientTests
{
    private sealed class DiskFull() : IOException("disk full");

    private readonly FakeLicensingServer server = new();
    private readonly MemoryLicenseStore store = new();
    private readonly LicensingClient client;

    public LicensingClientTests()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(server.Now);
        client = new LicensingClient(server.Integration, store, "1.0", server.Client(), () => now, preferTpm: false);
    }

    private static async Task<LicensingException> Fails(Func<Task> work) => await Assert.ThrowsAsync<LicensingException>(work);

    [Fact]
    public async Task ActivateRefreshAndDeactivate()
    {
        var lease = await client.ActivateAsync(" SYNTH-GOOD-KEY\n");
        Assert.Equal(1, lease.Sequence);
        Assert.Equal([new Entitlement("activation.limit", new EntitlementValue.Integer(2))], lease.Entitlements);
        var state = store.Load()!;
        Assert.Equal("act_1", state.Activation?.ActivationId);
        Assert.Equal(lease.Jws, state.PaidLease);
        Assert.NotNull(state.Manifest);
        Assert.Null(state.Pending);
        Assert.Equal(new HighWater { Sequence = 1, ObservedTime = server.Now, ActivationId = "act_1", LicenseId = "lic_1" }, state.HighWater);
        Assert.Equal(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", server.LastPlatform);

        Assert.Equal(2, (await client.RefreshAsync()).Sequence);
        Assert.Equal(2, store.Load()!.HighWater.Sequence);

        await client.DeactivateAsync();
        state = store.Load()!;
        Assert.Null(state.Activation);
        Assert.Null(state.PaidLease);
        Assert.Equal(0, server.SeatsUsed);
        Assert.Equal(2, state.HighWater.Sequence); // high-water marks survive deactivation
    }

    [Fact]
    public async Task LostResponseIsReplayedExactlyWithoutASecondActivation()
    {
        server.LoseNextResponse = true;
        Assert.Equal(LicensingFailure.Network, (await Fails(() => client.ActivateAsync("SYNTH-GOOD-KEY"))).Failure);
        Assert.Equal(OperationKind.Activate, store.Load()!.Pending?.Operation);
        Assert.Null(store.Load()!.Activation);

        await client.CompletePendingAsync();
        Assert.Equal(1, server.SeatsUsed);
        Assert.Equal(1, server.MutationsReceived); // the replay was answered from the stored response
        Assert.Equal("act_1", store.Load()!.Activation?.ActivationId);
        Assert.Null(store.Load()!.Pending);
    }

    [Fact]
    public async Task FailedCommitAfterSuccessKeepsTheRequestForReplay()
    {
        server.AfterMutation = () => store.FailCommits = new DiskFull();
        Assert.Equal(LicensingFailure.Storage, (await Fails(() => client.ActivateAsync("SYNTH-GOOD-KEY"))).Failure);
        server.AfterMutation = null;
        store.FailCommits = null;
        Assert.NotNull(store.Load()!.Pending); // the journal written before sending is intact

        var refreshed = await client.RefreshAsync(); // replays the activation first, then refreshes
        Assert.Equal(2, refreshed.Sequence);
        Assert.Equal(1, server.SeatsUsed);
        Assert.Equal(2, store.Load()!.HighWater.Sequence);
    }

    [Fact]
    public async Task NothingIsSentWhenTheJournalCannotBeWritten()
    {
        _ = client.State();
        store.FailCommits = new DiskFull();
        Assert.Equal(LicensingFailure.Storage, (await Fails(() => client.ActivateAsync("SYNTH-GOOD-KEY"))).Failure);
        Assert.Equal(0, server.MutationsReceived);
    }

    [Fact]
    public async Task RefusalClearsThePendingRequestAndRetryableFailureKeepsIt()
    {
        var refused = await Fails(() => client.ActivateAsync("SYNTH-BAD-KEY"));
        Assert.Equal((LicensingFailure.Refused, "invalid_product_key", "Synthetic invalid_product_key."), (refused.Failure, refused.Code, refused.Message));
        Assert.Null(store.Load()!.Pending);

        server.FailNext = (503, "service_unavailable", true);
        var failed = await Fails(() => client.ActivateAsync("SYNTH-GOOD-KEY"));
        Assert.Equal((LicensingFailure.Service, "service_unavailable"), (failed.Failure, failed.Code));
        Assert.NotNull(store.Load()!.Pending);
        Assert.True(failed.AllowsGrace);
        Assert.False(refused.AllowsGrace);
    }

    [Fact]
    public async Task RotatedSigningKeyIsFetchedOnce()
    {
        await client.ActivateAsync("SYNTH-GOOD-KEY");
        server.LeaseSigners["lease-2"] = new TestSigner();
        server.ActiveKid = "lease-2";
        server.ManifestGeneratedAt = "2026-08-01T12:00:00.000Z";
        var lease = await client.RefreshAsync();
        Assert.Equal("lease-2", lease.KeyId);
        var state = store.Load()!;
        Assert.True(state.ManifestRefetches.ContainsKey("lease-2"));
        Assert.Equal(2, new VerificationManifest(state.Manifest!, server.Integration).Keys.Count);
    }

    [Fact]
    public async Task ALeaseForAnotherInstallationIsRefused()
    {
        server.OverrideThumbprint = "sha256:someone-else";
        var error = await Fails(() => client.ActivateAsync("SYNTH-GOOD-KEY"));
        Assert.Equal((VerificationFailure.WrongScope, "lease installation"), (error.Verification?.Failure, error.Verification?.Detail));
        Assert.Null(store.Load()!.Activation);
        Assert.Null(store.Load()!.Pending); // a mis-scoped lease will not verify on replay either
    }

    [Fact]
    public async Task TrialStartAndRefresh()
    {
        Assert.Equal(LicensingFailure.NoTrial, (await Fails(() => client.RefreshTrialAsync())).Failure);
        var lease = await client.StartTrialAsync();
        var trial = Assert.IsType<LeaseKind.Trial>(lease.Kind);
        Assert.Equal(("trl_1", 1L), (trial.TrialId, trial.Revision));
        Assert.Equal("trial-cred", store.Load()!.TrialRefreshCredential);
        Assert.Equal(2, (await client.RefreshTrialAsync()).Sequence);
    }

    [Fact]
    public async Task ATrialAfterAPaidKeyAndASecondActivationEachStartTheirOwnSequence()
    {
        await client.ActivateAsync("SYNTH-GOOD-KEY");
        await client.RefreshAsync();
        await client.RefreshAsync();
        await client.DeactivateAsync();
        Assert.Equal(1, (await client.StartTrialAsync()).Sequence);
        Assert.Equal(1, (await client.ActivateAsync("SYNTH-GOOD-KEY")).Sequence); // a new activation numbers its leases from 1
        var marks = store.Load()!.HighWater;
        Assert.Equal((1L, (long?)1, "act_2", "trl_1"), (marks.Sequence, marks.TrialSequence, marks.ActivationId, marks.TrialId));
    }
}
