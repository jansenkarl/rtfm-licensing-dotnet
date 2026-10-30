using System.Text.Json.Nodes;
using RTFM.Licensing;

namespace RTFM.Licensing.Tests;

public class RTFMLicenseTests
{
    private const long Day = 86400;
    private readonly FakeLicensingServer server = new();
    private readonly MemoryLicenseStore store = new();
    private long clock;
    private readonly RTFMLicense license;

    public RTFMLicenseTests()
    {
        clock = server.Now;
        license = new RTFMLicense(server.Integration, store, "1.0", server.Client(), () => DateTimeOffset.FromUnixTimeSeconds(clock), preferTpm: false);
    }

    private void At(long seconds) => clock = server.Now = seconds;

    [Fact]
    public async Task NothingStoredMeansNotActivated()
    {
        Assert.IsType<LicenseStatus.Checking>(license.Status);
        await license.StartAsync();
        Assert.Equal(new LicenseStatus.NotLicensed(new NotLicensedReason(NotLicensedKind.NotActivated)), license.Status);
        Assert.False(license.Status.AllowsUse);
    }

    [Fact]
    public async Task ActivatedKeyWorksOfflineThenInGraceThenExpires()
    {
        var changes = 0;
        license.StatusChanged += (_, _) => changes++;
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        var info = Assert.IsType<LicenseStatus.Licensed>(license.Status).Info;
        Assert.Equal(new EntitlementValue.Integer(2), info.Entitlement("activation.limit"));
        Assert.False(info.InGrace);
        Assert.Null(info.Term);
        Assert.Equal(1, changes);

        server.Offline = true;
        clock = server.Now + 80 * Day; // past refresh_after: tries quietly, fails, still licensed
        await license.StartAsync();
        Assert.True(license.Status.AllowsUse);
        clock = server.Now + 95 * Day; // past exp, within grace, offline
        await license.StartAsync();
        Assert.True(Assert.IsType<LicenseStatus.Licensed>(license.Status).Info.InGrace);
        clock = server.Now + 105 * Day; // past grace
        await license.StartAsync();
        Assert.Equal(NotLicensedKind.Expired, Assert.IsType<LicenseStatus.NotLicensed>(license.Status).Reason.Kind);

        server.Offline = false; // back online: the lease renews
        At(server.Now + 105 * Day);
        await license.StartAsync();
        Assert.False(Assert.IsType<LicenseStatus.Licensed>(license.Status).Info.InGrace);
    }

    [Fact]
    public async Task AKeyRefusedOnRenewalStopsAndIsForgotten()
    {
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        At(server.Now + 80 * Day);
        server.FailNext = (401, "invalid_activation_credentials", false);
        await license.StartAsync();
        Assert.Equal(new NotLicensedReason(NotLicensedKind.Refused, "invalid_activation_credentials"), Assert.IsType<LicenseStatus.NotLicensed>(license.Status).Reason);
        Assert.Null(store.Load()!.PaidLease);
        await license.StartAsync();
        Assert.Equal(NotLicensedKind.NotActivated, Assert.IsType<LicenseStatus.NotLicensed>(license.Status).Reason.Kind);
    }

    [Fact]
    public async Task SubscriptionTermIsShownAndEnds()
    {
        server.Term = new JsonObject { ["type"] = "subscription", ["ends_at"] = "2026-08-30T00:00:00.000Z", ["access_ends_at"] = "2026-09-02T00:00:00.000Z" };
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        var term = Assert.IsType<LicenseStatus.Licensed>(license.Status).Info.Term!;
        Assert.Equal((TermKind.Subscription, Timestamp.Parse("2026-08-30T00:00:00.000Z")!.Value), (term.Kind, term.EndsAt));
        server.Offline = true;
        clock = Timestamp.Parse("2026-09-02T00:00:00.000Z")!.Value.ToUnixTimeSeconds();
        await license.StartAsync();
        Assert.Equal(NotLicensedKind.TermEnded, Assert.IsType<LicenseStatus.NotLicensed>(license.Status).Reason.Kind);
    }

    [Fact]
    public async Task AClockSetBackIsNoticedBeyondASmallTolerance()
    {
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        server.Offline = true;
        clock = server.Now + 10 * Day;
        await license.StartAsync();
        clock = server.Now + 10 * Day - 100;
        await license.StartAsync();
        Assert.True(license.Status.AllowsUse); // a small step back (time sync) is tolerated
        clock = server.Now + 5 * Day;
        await license.StartAsync();
        Assert.Equal(NotLicensedKind.ClockRolledBack, Assert.IsType<LicenseStatus.NotLicensed>(license.Status).Reason.Kind);
    }

    [Fact]
    public async Task TrialAndDeactivation()
    {
        await license.StartTrialAsync();
        Assert.Equal(Timestamp.Parse("2026-12-31T00:00:00.000Z"), Assert.IsType<LicenseStatus.Trial>(license.Status).Info.EndsAt);
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        Assert.IsType<LicenseStatus.Licensed>(license.Status); // a paid key takes precedence over a trial
        await license.DeactivateAsync();
        Assert.IsType<LicenseStatus.Trial>(license.Status);
        Assert.Equal(0, server.SeatsUsed);
    }

    [Fact]
    public async Task CheckNowRenewsBeforeTheLeaseIsDue()
    {
        await license.ActivateAsync("SYNTH-GOOD-KEY");
        At(server.Now + 3600);
        await license.StartAsync();
        Assert.Equal(server.Now - 3600, Assert.IsType<LicenseStatus.Licensed>(license.Status).Info.IssuedAt.ToUnixTimeSeconds()); // start() does not renew early
        await license.CheckNowAsync();
        Assert.Equal(server.Now, Assert.IsType<LicenseStatus.Licensed>(license.Status).Info.IssuedAt.ToUnixTimeSeconds());
        Assert.Equal(2, store.Load()!.HighWater.Sequence);
    }

    [Fact]
    public void MalformedTermIsRefused()
    {
        Assert.Throws<VerificationException>(() => LeaseVerifier.ParseTerm(Json.ParseObject("""{"type":"monthly"}"""u8)));
        Assert.Null(LeaseVerifier.ParseTerm(null));
    }
}
