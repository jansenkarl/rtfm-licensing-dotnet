using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

public class StoreTests
{
    private static LicenseState Sample()
    {
        using var identity = InstallationIdentity.Generate(preferTpm: false);
        return new LicenseState
        {
            InstallationKey = identity.Reference, Activation = new ActivationRecord("act_synthetic", "synthetic-credential"), PaidLease = "a.b.c",
            HighWater = new HighWater { Sequence = 4, RevocationEpoch = 1, ObservedTime = 1_785_499_200 },
            ManifestRefetches = new Dictionary<string, long> { ["kid-2"] = 1_785_499_200 },
            Pending = new PendingOperation(OperationKind.Activate, "/v1/activations", "{}"u8.ToArray(), "idem-1"),
        };
    }

    private static void AssertSame(LicenseState expected, LicenseState? actual) =>
        Assert.Equal(LicenseStateCoding.Encode(expected), LicenseStateCoding.Encode(actual!));

    [Fact]
    public void MemoryStoreRoundTripAndFailedCommitKeepsThePreviousState()
    {
        var store = new MemoryLicenseStore();
        Assert.Null(store.Load());
        var first = Sample();
        store.Commit(first);
        AssertSame(first, store.Load());
        store.FailCommits = new IOException("disk full");
        Assert.Throws<IOException>(() => store.Commit(first with { HighWater = first.HighWater with { Sequence = 5 } }));
        AssertSame(first, store.Load()); // a failed commit leaves the earlier state whole
        store.FailCommits = null;
        store.Erase();
        Assert.Null(store.Load());
    }

    [Fact]
    public void FileStoreRoundTripReplaceAndErase()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtfm-licensing-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileLicenseStore(Path.Combine(folder, "license-state.dat"));
            Assert.Null(store.Load());
            var state = Sample();
            store.Commit(state);
            AssertSame(state, store.Load());
            var next = state with { HighWater = state.HighWater with { Sequence = 10 } };
            store.Commit(next);
            AssertSame(next, new FileLicenseStore(store.Path).Load());
            Assert.Single(Directory.GetFiles(folder)); // no temporary files left behind
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));
            }
            else
            {
                Assert.DoesNotContain("act_synthetic", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Path))); // DPAPI-encrypted
            }
            store.Erase();
            Assert.Null(store.Load());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void UnreadableStateIsNotTrusted()
    {
        Assert.Throws<StoreException>(() => LicenseStateCoding.Decode("not json"u8.ToArray()));
    }

    [Fact]
    public void AppFolderNamesMustBePlain()
    {
        Assert.Throws<ArgumentException>(() => FileLicenseStore.ForApp("..", "App"));
        Assert.Throws<ArgumentException>(() => FileLicenseStore.ForApp("Northwind", "a/b"));
        Assert.EndsWith(Path.Combine("Northwind", "Tidepool", "license-state.dat"), FileLicenseStore.ForApp("Northwind", "Tidepool").Path);
    }

    [Fact]
    public void AcceptingALeaseRaisesButNeverLowersHighWater()
    {
        var state = Sample() with { HighWater = new HighWater { Sequence = 9, RevocationEpoch = 3, TrialRevision = 2, ObservedTime = 100, ActivationId = "act", LicenseId = "lic" } };
        var lease = new Lease
        {
            Kind = new LeaseKind.Paid("lic", "act", 5), Jws = "new.lease.jws", KeyId = "k", Sequence = 7, IssuedAt = 0, NotBefore = 0, RefreshAfter = 1,
            ExpiresAt = 2, GraceUntil = 3, Entitlements = [],
        };
        var next = state.Accepting(lease).Observing(50);
        Assert.Equal("new.lease.jws", next.PaidLease);
        Assert.Equal(new HighWater { Sequence = 9, RevocationEpoch = 5, TrialRevision = 2, ObservedTime = 100, ActivationId = "act", LicenseId = "lic" }, next.HighWater);
    }

    [Fact]
    public void InstallationIdentitySignsChallengeProofs()
    {
        using var identity = InstallationIdentity.Generate(preferTpm: false);
        using var restored = InstallationIdentity.Open(identity.Reference);
        Assert.Equal(identity.Thumbprint, restored.Thumbprint);
        Assert.Equal("software", identity.Reference.Storage);
        var message = "rtfm-device-challenge-v1:chg_synthetic:nonce";
        using var verifier = identity.PublicKey.CreateVerifier();
        Assert.True(verifier.VerifyData(System.Text.Encoding.UTF8.GetBytes(message), Base64Url.Decode(identity.Sign(message))!,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>Creates a key in the TPM and signs with it. Windows only, and only when asked
    /// (<c>RTFM_TPM_TESTS=1</c>): a PC without a usable TPM falls back to software, which the test reports.</summary>
    [Fact]
    public void TpmKeyWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RTFM_TPM_TESTS") != "1") return;
        using var identity = InstallationIdentity.Generate(preferTpm: true);
        try
        {
            Assert.Contains(identity.Reference.Storage, new[] { "tpm", "software" });
            using var reopened = InstallationIdentity.Open(identity.Reference);
            Assert.Equal(identity.Thumbprint, reopened.Thumbprint);
            Assert.NotEmpty(reopened.Sign("message"));
            Console.WriteLine($"Installation key storage: {identity.Reference.Storage}");
        }
        finally { InstallationIdentity.Delete(identity.Reference); }
    }
}
