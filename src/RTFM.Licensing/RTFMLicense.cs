using System.ComponentModel;
using System.Reflection;

namespace RTFM.Licensing;

/// <summary>The SDK's main object: one per app. Call <see cref="StartAsync"/> at launch, then read
/// <see cref="Status"/>. <see cref="StatusChanged"/> and <see cref="PropertyChanged"/> are raised on the
/// synchronization context the object was created on (the UI thread in WPF, WinForms and WinUI).</summary>
/// <example>
/// <code>
/// var license = RTFMLicense.ForApp("Northwind", "Tidepool");   // reads app-integration.json next to the app
/// await license.StartAsync();
/// if (!license.Status.AllowsUse) ShowEnterKeyWindow(license);
/// </code>
/// </example>
public sealed class RTFMLicense : INotifyPropertyChanged, IDisposable
{
    /// <summary>Refusals that mean the key no longer works on this computer, so its stored lease is dropped.</summary>
    internal static readonly HashSet<string> EndedActivationCodes = ["invalid_activation_credentials", "license_term_ended"];
    internal static readonly HashSet<string> EndedTrialCodes = ["trial_refresh_credential_invalid", "trial_not_found", "trial_not_lease_eligible", "trial_converted"];

    private readonly LicensingClient client;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private LicenseStatus status = new LicenseStatus.Checking();

    /// <summary>The current status. <see cref="LicenseStatus.Checking"/> until <see cref="StartAsync"/> finishes.</summary>
    public LicenseStatus Status => status;

    /// <summary>The App integration file in use.</summary>
    public AppIntegration Integration => client.Integration;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    public event EventHandler<LicenseStatus>? StatusChanged;

    /// <summary>An object for <paramref name="integration"/>, keeping its state in <paramref name="store"/>.</summary>
    public RTFMLicense(AppIntegration integration, ILicenseStore store, string appVersion, HttpClient? http = null, Func<DateTimeOffset>? now = null,
        bool preferTpm = true)
    {
        client = new LicensingClient(integration, store, appVersion, http, now, preferTpm);
    }

    /// <summary>Reads <paramref name="file"/> (the App integration file) from the app's folder, and keeps the license
    /// for this Windows user in <c>%LOCALAPPDATA%\{publisher}\{app}</c>, encrypted with DPAPI. The app version comes
    /// from the entry assembly.</summary>
    public static RTFMLicense ForApp(string publisher, string app, string file = "app-integration.json")
    {
        var path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path)) throw new FileNotFoundException($"{file} is not in the app's folder. Add it to the project and set Copy to Output Directory.", path);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0";
        return new RTFMLicense(AppIntegration.FromFile(path), FileLicenseStore.ForApp(publisher, app), version);
    }

    /// <summary>Finishes any request left pending, renews the lease quietly once it is due, and updates
    /// <see cref="Status"/>. Never blocks offline use: without a network the stored lease decides, including its
    /// grace period.</summary>
    public Task StartAsync(CancellationToken cancel = default) => CheckAsync(force: false, cancel);

    /// <summary>Renews the lease now, whether or not it is due, for a "Check now" button.</summary>
    public Task CheckNowAsync(CancellationToken cancel = default) => CheckAsync(force: true, cancel);

    private async Task CheckAsync(bool force, CancellationToken cancel)
    {
        var network = NetworkState.NotChecked;
        NotLicensedReason? refusal = null;
        try { await client.CompletePendingAsync(cancel).ConfigureAwait(false); }
        catch (LicensingException error) when (error.AllowsGrace) { network = NetworkState.Unavailable; }
        catch (LicensingException) { }
        var now = client.UnixNow;
        foreach (var kind in new[] { LeaseType.Paid, LeaseType.Trial })
        {
            long? due;
            try { due = client.RefreshAfter(kind); } catch (LicensingException) { continue; }
            if (due is null || (!force && now < due) || network == NetworkState.Unavailable) continue;
            try
            {
                if (kind == LeaseType.Paid) await client.RefreshAsync(cancel).ConfigureAwait(false);
                else await client.RefreshTrialAsync(cancel).ConfigureAwait(false);
                network = NetworkState.Available;
            }
            catch (LicensingException error) when (error.AllowsGrace) { network = NetworkState.Unavailable; }
            catch (LicensingException error) when (error.Failure == LicensingFailure.Refused)
            {
                network = NetworkState.Available;
                var ended = (kind == LeaseType.Paid ? EndedActivationCodes : EndedTrialCodes).Contains(error.Code!);
                if (ended)
                {
                    try { client.Forget(activation: kind == LeaseType.Paid, trial: kind == LeaseType.Trial); } catch (LicensingException) { }
                    if (kind == LeaseType.Paid)
                    {
                        refusal = error.Code == "license_term_ended" ? new NotLicensedReason(NotLicensedKind.TermEnded) : new NotLicensedReason(NotLicensedKind.Refused, error.Code);
                    }
                }
            }
            catch (LicensingException) { }
            if (kind == LeaseType.Paid && network == NetworkState.Available) break;
        }
        try { client.RecordObservedTime(); } catch (LicensingException) { }
        UpdateStatus(network, refusal);
    }

    /// <summary>Activates a product key on this computer. Throws <see cref="LicensingException"/>; for
    /// <see cref="LicensingFailure.Refused"/> its message is safe to show.</summary>
    public async Task ActivateAsync(string productKey, string? deviceLabel = null, CancellationToken cancel = default)
    {
        await client.ActivateAsync(productKey, deviceLabel, cancel).ConfigureAwait(false);
        UpdateStatus(NetworkState.Available);
    }

    /// <summary>Releases this computer's activation so the key can be activated on another.</summary>
    public async Task DeactivateAsync(CancellationToken cancel = default)
    {
        await client.DeactivateAsync(cancel).ConfigureAwait(false);
        UpdateStatus(NetworkState.Available);
    }

    /// <summary>Starts a trial, or resumes this computer's trial.</summary>
    public async Task StartTrialAsync(CancellationToken cancel = default)
    {
        await client.StartTrialAsync(cancel).ConfigureAwait(false);
        UpdateStatus(NetworkState.Available);
    }

    private void UpdateStatus(NetworkState network, NotLicensedReason? refusal = null)
    {
        LicenseStatus evaluated;
        try { evaluated = client.CurrentStatus(network); }
        catch (LicensingException) { evaluated = new LicenseStatus.NotLicensed(new NotLicensedReason(NotLicensedKind.InvalidStoredLicense)); }
        var next = refusal is not null && evaluated is LicenseStatus.NotLicensed ? new LicenseStatus.NotLicensed(refusal) : evaluated;
        if (next == status) return;
        status = next;
        void Raise(object? _)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            StatusChanged?.Invoke(this, next);
        }
        if (context is null || context == SynchronizationContext.Current) Raise(null); else context.Post(Raise, null);
    }

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
