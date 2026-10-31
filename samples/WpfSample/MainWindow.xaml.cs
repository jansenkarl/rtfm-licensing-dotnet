using System.Windows;
using System.Windows.Controls;
using RTFM.Licensing;

namespace WpfSample;

public partial class MainWindow : Window
{
    private readonly RTFMLicense license;

    public MainWindow(RTFMLicense license)
    {
        InitializeComponent();
        this.license = license;
        ProductText.Text = $"{license.Integration.ProductCode} · {license.Integration.ProductVersionCode} ({license.Integration.Environment})";
        BuildText.Text = $"Windows {Environment.OSVersion.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · {Environment.MachineName}";
        license.StatusChanged += (_, status) => Show(status);
        Loaded += async (_, _) => await Run(() => license.StartAsync());
    }

    private void Show(LicenseStatus status)
    {
        (StatusText.Text, DetailText.Text) = status switch
        {
            LicenseStatus.Licensed { Info: var info } => ("Licensed", Licensed(info)),
            LicenseStatus.Trial { Info: var trial } => ($"Trial until {trial.EndsAt.LocalDateTime:d}",
                trial.InGrace ? "Offline. The trial keeps working until it can be confirmed again." : "Enter a key at any time to keep using the app."),
            LicenseStatus.NotLicensed { Reason: var reason } => ("Not licensed", NotLicensed(reason)),
            _ => ("Checking…", ""),
        };
        EnterKeyPanel.Visibility = status is LicenseStatus.NotLicensed or LicenseStatus.Trial ? Visibility.Visible : Visibility.Collapsed;
        LicensedPanel.Visibility = status is LicenseStatus.Licensed ? Visibility.Visible : Visibility.Collapsed;
        TrialButton.Visibility = status is LicenseStatus.NotLicensed ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Licensed(LicenseInfo info)
    {
        var term = info.Term is { } t ? $"{(t.Kind == TermKind.Subscription ? "Renews" : "Ends")} {t.EndsAt.LocalDateTime:d}. " : "";
        return info.InGrace
            ? $"{term}Offline. Keeps working until {info.GraceUntil.LocalDateTime:g}; connect to confirm the key."
            : $"{term}Last confirmed {info.IssuedAt.LocalDateTime:g}.";
    }

    private static string NotLicensed(NotLicensedReason reason) => reason.Kind switch
    {
        NotLicensedKind.NotActivated => "Enter your product key, or start a trial.",
        NotLicensedKind.Expired => "The license could not be confirmed. Connect to the internet and open the app again.",
        NotLicensedKind.TermEnded => "This key's term has ended. Enter a new key.",
        NotLicensedKind.TrialEnded => "The trial has ended. Enter a product key to keep using the app.",
        NotLicensedKind.Refused => $"This key no longer works on this computer ({reason.Code}). Enter a key.",
        NotLicensedKind.ClockRolledBack => "This computer's date and time look wrong. Correct them, then open the app again.",
        _ => "The saved license could not be read. Enter your key again.",
    };

    private void OnKeyChanged(object sender, TextChangedEventArgs e) => ActivateButton.IsEnabled = KeyBox.Text.Trim().Length > 0;

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        if (await Run(() => license.ActivateAsync(KeyBox.Text, deviceLabel: Environment.MachineName))) KeyBox.Clear();
    }

    private async void OnStartTrial(object sender, RoutedEventArgs e) => await Run(() => license.StartTrialAsync());

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await Run(() => license.CheckNowAsync());

    private async void OnDeactivate(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Deactivate this computer? The key can then be activated on another computer.", Title,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) await Run(() => license.DeactivateAsync());
    }

    // Runs one licensing call with the buttons disabled, showing its error. Refused and network messages are written
    // for customers; anything else gets a general message.
    private async Task<bool> Run(Func<Task> operation)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            await operation();
            return true;
        }
        catch (LicensingException error)
        {
            ErrorText.Text = error.Failure is LicensingFailure.Refused or LicensingFailure.Network
                ? error.Message
                : $"Something went wrong ({error.Failure}{(error.Code is null ? "" : $", {error.Code}")}). Try again later.";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        finally
        {
            IsEnabled = true;
            Cursor = null;
            Show(license.Status);
        }
    }
}
