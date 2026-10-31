# Getting started

This guide takes a Windows app from no licensing to selling keys: set up the product in the RTFM License Manager, add
the package, gate the app, test with a real key, then ship. The examples use WPF; WinForms and WinUI use the same
calls.

## 1. Set up your product in the License Manager

1. **Create a product** for your app, or open an existing one. A product has versions; each version holds the rules
   for its keys and trials: how many PCs a key covers, how many times a key can be moved to another PC, and how long a
   trial lasts.
2. In **Edit version**, make sure **Platforms** includes **Windows**. A version that lists Mac and Windows sells one
   key for both: it activates on either, and each Mac or PC uses one seat.
3. **Enable the version** so it accepts new keys.
4. Optional: add **custom fields** to the version, such as an edition or a feature flag. Each has a code (lowercase,
   for example `edition` or `feature.export`) and is signed into every lease, so the app can trust it.
5. Choose **App integration** on the version and **Download JSON**. The file holds only public values: the licensing
   service's address, the product and version codes, and the root key your app uses to check every license. Nothing
   in it is secret.
6. Choose **New key** to make a key for yourself to test with.

## 2. Add the package

```
dotnet add package RTFMDesign.Licensing
```

Requirements: .NET 8 or later, Windows 10 or 11. The package also builds and runs on macOS and Linux (with a
software key and a file readable only by the user), which is useful for tests.

## 3. Add the App integration file

Rename the downloaded file to `app-integration.json`, add it to the project, and have it copied next to the app:

```xml
<ItemGroup>
  <None Update="app-integration.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

In Visual Studio, that's **Properties → Copy to Output Directory → Copy if newer** on the file.

## 4. Gate the app

Create one `RTFMLicense` for the whole app, start it at launch, and open the main window only when `Status.AllowsUse`
is true. In `App.xaml`, replace `StartupUri="MainWindow.xaml"` with `Startup="OnStartup"`, then:

```csharp
using System.Windows;
using RTFM.Licensing;

namespace Tidepool;

public partial class App : Application
{
    private RTFMLicense? license;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // until the main window is open
        // Reads app-integration.json beside the app; keeps the license in %LOCALAPPDATA%\Northwind\Tidepool.
        license = RTFMLicense.ForApp("Northwind", "Tidepool");
        await license.StartAsync();
        if (!license.Status.AllowsUse && new EnterKeyWindow(license).ShowDialog() != true)
        {
            Shutdown();
            return;
        }
        MainWindow = new MainWindow(license);
        MainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        license?.Dispose();
        base.OnExit(e);
    }
}
```

Create the `RTFMLicense` on the UI thread, as here: its events then arrive on the UI thread.

`StartAsync()` never blocks offline use. It finishes any request a previous launch left unfinished, renews the lease
if it is due, and sets `Status`. Without a network the stored lease decides, including its grace period.

Use publisher and app names that won't change between versions: they name the folder that holds the license.
`ForApp` throws if `app-integration.json` is missing or malformed, which you see the first time you run the app.

## 5. Ask for a key

`EnterKeyWindow.xaml`:

```xml
<Window x:Class="Tidepool.EnterKeyWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Activate Tidepool" Width="420" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen">
    <StackPanel Margin="20">
        <TextBlock x:Name="ReasonText" TextWrapping="Wrap" Margin="0,0,0,12" />
        <Label Content="_Product key" Target="{Binding ElementName=KeyBox}" Padding="0,0,0,4" />
        <TextBox x:Name="KeyBox" />
        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <Button Content="_Activate" IsDefault="True" Padding="12,4" Click="OnActivate" />
            <Button Content="Start free _trial" Padding="12,4" Margin="8,0,0,0" Click="OnStartTrial" />
        </StackPanel>
        <TextBlock x:Name="MessageText" TextWrapping="Wrap" Margin="0,12,0,0" />
    </StackPanel>
</Window>
```

`EnterKeyWindow.xaml.cs`:

```csharp
using System.Windows;
using RTFM.Licensing;

namespace Tidepool;

public partial class EnterKeyWindow : Window
{
    private readonly RTFMLicense license;

    public EnterKeyWindow(RTFMLicense license)
    {
        InitializeComponent();
        this.license = license;
        ReasonText.Text = license.Status is LicenseStatus.NotLicensed { Reason: var reason }
            ? LicenseMessages.Explain(reason)
            : "Enter your product key.";
    }

    private void OnActivate(object sender, RoutedEventArgs e) => Run(() => license.ActivateAsync(KeyBox.Text, Environment.MachineName));

    private void OnStartTrial(object sender, RoutedEventArgs e) => Run(() => license.StartTrialAsync());

    private async void Run(Func<Task> operation)
    {
        IsEnabled = false;
        MessageText.Text = "";
        try
        {
            await operation();
            if (license.Status.AllowsUse) DialogResult = true;
        }
        catch (LicensingException error) { MessageText.Text = LicenseMessages.Describe(error); }
        finally { IsEnabled = true; }
    }
}

public static class LicenseMessages
{
    /// <summary>A message for a failed activation, deactivation or trial start.</summary>
    public static string Describe(LicensingException error) => error.Failure switch
    {
        LicensingFailure.Refused => error.Message, // written by the licensing service, safe to show
        LicensingFailure.Network or LicensingFailure.Service => "Couldn't reach the licensing service. Check your connection and try again.",
        _ => "Something went wrong with the license. Please try again.",
    };

    /// <summary>Why the app is asking for a key.</summary>
    public static string Explain(NotLicensedReason reason) => reason.Kind switch
    {
        NotLicensedKind.NotActivated => "Enter your product key, or start a free trial.",
        NotLicensedKind.Expired => "Your license needs to be renewed online. Connect to the internet and reopen the app.",
        NotLicensedKind.TermEnded => "Your license has ended. Renew it to keep using the app.",
        NotLicensedKind.TrialEnded => "Your trial has ended. Enter a product key to keep using the app.",
        NotLicensedKind.Refused => "This key no longer works on this PC. Enter a product key.",
        NotLicensedKind.ClockRolledBack => "Your PC's date and time look wrong. Correct them in Settings, then reopen the app.",
        _ => "Your license needs to be activated again.",
    };
}
```

The device label (`Environment.MachineName` here) is what you and the customer see for this PC in the License
Manager and on the self-service page. Remove the trial button if you don't offer trials.

## 6. Show the license and let customers move it

Customers need to see what they have and to release a PC when they replace it. A **License** menu in the main window
does both, and the window asks for a key again if the license stops allowing use while the app is open (after
**Deactivate**, or when **Check now** finds the key revoked):

```xml
<DockPanel>
    <Menu DockPanel.Dock="Top">
        <MenuItem Header="_License">
            <MenuItem Header="_Check now" Click="OnCheckNow" />
            <MenuItem Header="_Deactivate this PC…" Click="OnDeactivate" />
            <Separator />
            <MenuItem Header="_Lost a PC? Free it online" Click="OnSelfService" />
        </MenuItem>
    </Menu>
    <StatusBar DockPanel.Dock="Bottom">
        <TextBlock x:Name="LicenseText" />
    </StatusBar>
    <!-- Your app -->
</DockPanel>
```

```csharp
using System.Diagnostics;
using System.Windows;
using RTFM.Licensing;

namespace Tidepool;

public partial class MainWindow : Window
{
    private readonly RTFMLicense license;

    public MainWindow(RTFMLicense license)
    {
        InitializeComponent();
        this.license = license;
        LicenseText.Text = Summary(license.Status);
        license.StatusChanged += OnStatusChanged;
        Closed += (_, _) => license.StatusChanged -= OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, LicenseStatus status)
    {
        LicenseText.Text = Summary(status);
        if (status.AllowsUse || !IsVisible) return;
        if (new EnterKeyWindow(license) { Owner = this }.ShowDialog() != true) Close();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await license.CheckNowAsync();

    private async void OnDeactivate(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Deactivate this PC? You can then activate your key on another PC.", "Deactivate",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        try { await license.DeactivateAsync(); }
        catch (LicensingException error) { MessageBox.Show(this, LicenseMessages.Describe(error), "Deactivate"); }
    }

    // Your product's self-service page (see below).
    private void OnSelfService(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://lm.rtfmdesign.com/deactivate/PRODUCT-CODE") { UseShellExecute = true });

    private static string Summary(LicenseStatus status) => status switch
    {
        LicenseStatus.Licensed { Info.InGrace: true } licensed =>
            $"Licensed. The licensing service couldn't be reached; the app works until {licensed.Info.GraceUntil.LocalDateTime:g}.",
        LicenseStatus.Licensed { Info.Term: { } term } =>
            $"Licensed. {(term.Kind == TermKind.Subscription ? "Renews" : "Ends")} {term.EndsAt.LocalDateTime:d}.",
        LicenseStatus.Licensed => "Licensed.",
        LicenseStatus.Trial { Info: var trial } => $"Trial. Ends {trial.EndsAt.LocalDateTime:g}.",
        LicenseStatus.NotLicensed => "Not licensed.",
        _ => "Checking your license…",
    };
}
```

**Deactivate** releases this PC's seat so the key can be activated on another PC. A version can limit how many times
a key may be moved; past the limit the service refuses with `deactivation_limit_reached`. When a customer no longer
has the old PC (reset, replaced or lost without deactivating), they free it themselves on the License Manager's
self-service page, `https://lm.rtfmdesign.com/deactivate/<product code>`: they enter the key, see its devices and free
one, which counts against the key's deactivation limit. You can also deactivate it from the key's page in the License
Manager, which doesn't count. A PC that hasn't checked in for 120 days (a version setting) is freed automatically. The
page and the 120 days can be turned off per version in Edit version.

## 7. Read custom fields

Signed values, including your custom fields, are on the license:

```csharp
if (license.Status is LicenseStatus.Licensed { Info: var info } && info.Entitlement("edition") is EntitlementValue.Text { Value: var edition })
{
    // "pro", "standard", …
}
```

A value is `EntitlementValue.Text`, `Integer`, `Boolean` or `DateTime`. The system value `activation.limit` is the
number of devices the key covers.

## 8. Test it

Run the app, enter your test key, and check:

- the app opens and the status bar shows "Licensed";
- close the app, disconnect from the network, reopen: still licensed;
- **License → Deactivate this PC**, then activate the same key again.

You can also try the key without building your app, with the samples in this repository:

```powershell
git clone https://github.com/jansenkarl/rtfm-licensing-dotnet
cd rtfm-licensing-dotnet
Copy-Item "$HOME\Downloads\app-integration.json" samples\WpfSample\app-integration.json
dotnet run --project samples\WpfSample
```

Each sample keeps its license apart from your app's, so it uses a seat of its own: deactivate it when you're done.

## 9. Ship

- Keep the publisher and app names passed to `ForApp` the same in every release, so updates find the license.
- Ship the App integration file for the environment your customers use (its `environment` field says which), next to
  the app's executable. Installers that put the app in `Program Files` are fine: the license lives in each user's
  `%LOCALAPPDATA%`, not beside the app.
- The license belongs to the Windows user, so each user on a shared PC activates and uses a seat.
- Resetting Windows, or clearing the TPM, removes the installation's key; the customer then frees the old activation
  on the self-service page and activates again.
- To sell keys, connect your store in the License Manager.
