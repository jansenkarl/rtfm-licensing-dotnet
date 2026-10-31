# RTFM licensing: integration guide for AI coding assistants

You are adding RTFM licensing to a Windows app with the NuGet package `RTFMDesign.Licensing` (namespace
`RTFM.Licensing`). This page is the complete instruction set. Follow it as written. Where it conflicts with what you
would otherwise do, this page wins.

Package version: 0.1.0. Human docs: https://github.com/jansenkarl/rtfm-licensing-dotnet/tree/main/docs

## What you need from the developer

1. **The App integration file**, downloaded from the RTFM License Manager (the version's **App integration →
   Download JSON**). It is public and contains no secrets. Its `"schema"` is `"rtfm.app-integration.v1"`. If the
   project has no such file, stop and ask the developer for it. **Never write, edit or invent this file**: its
   `rootPublicJwk` is the key every license is checked against.
2. Whether the app offers a **free trial** (ask if it isn't clear).
3. A **publisher name and app name** for the license folder, such as the company and product names. Ask if they
   aren't clear. They must never change between versions.

You don't need a product key to write the code. The developer tests with their own key.

## Rules

- .NET 8 or later. The code below is for WPF; for WinForms or WinUI, keep the same structure and calls.
- Use only the public API below. Do not implement license checks, signature verification, network calls or storage
  yourself, and do not call the licensing service directly.
- Create exactly **one** `RTFMLicense` per app, with `RTFMLicense.ForApp(...)`, **on the UI thread** at startup, and
  pass it to the windows that need it. Never create one per window.
- Call `await license.StartAsync()` once at launch. Do not call it in a timer or on every window activation.
- Gate the app on `license.Status.AllowsUse`. Do not invent other checks, and do not cache the status yourself (in
  settings, the registry or elsewhere).
- Never block launch on the network, and never show an error just because the PC is offline: the SDK keeps the app
  licensed offline through the signed lease and its grace period.
- Never log, print, store or send the product key the customer types. Pass it straight to `ActivateAsync`. Do not put
  product keys in code, tests, settings or sample data.
- Show `LicensingException.Message` to the customer as it is only when `Failure` is `LicensingFailure.Refused`: it is
  written for customers. Show a fixed "couldn't reach the licensing service" message for `Network` and `Service`.
- Always give the customer a way to **deactivate** (move the key to another PC), with "Do this before you reset or
  replace this PC." in its confirmation. If the developer's prompt gives the address of the License Manager's
  self-service page, add a "Lost a PC? Free it online" menu item that opens it; otherwise leave it out.
- Do not edit `app-integration.json`, and do not add the package's samples to the app.

## Step 1. Add the package

```
dotnet add package RTFMDesign.Licensing
```

Then, in the app's `.csproj`, set the reference to `Version="0.1.*"` so updates stay within 0.1:
`<PackageReference Include="RTFMDesign.Licensing" Version="0.1.*" />`.

## Step 2. Copy the App integration file next to the app

The file must be named `app-integration.json`, be in the app project, and be copied to the output folder:

```xml
<ItemGroup>
  <None Update="app-integration.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

If the app has an installer (MSIX, WiX, Inno Setup or similar), make sure it installs the file next to the
executable.

## Step 3. Write the code

Use the app's own namespace in place of `Tidepool`, and the developer's publisher and app names in `ForApp`.

**App startup.** In `App.xaml`, replace `StartupUri="MainWindow.xaml"` with `Startup="OnStartup"`. In
`App.xaml.cs`, keep anything the app already does at startup, and open the main window this way:

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
        license = RTFMLicense.ForApp("Northwind", "Tidepool"); // the developer's publisher and app names; never change them
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

If the main window's constructor takes other arguments, add `RTFMLicense license` to it rather than replacing them.

**Enter-key window.** Add `EnterKeyWindow.xaml`:

```xml
<Window x:Class="Tidepool.EnterKeyWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Activate" Width="420" SizeToContent="Height" ResizeMode="NoResize"
        WindowStartupLocation="CenterScreen">
    <StackPanel Margin="20">
        <TextBlock x:Name="ReasonText" TextWrapping="Wrap" Margin="0,0,0,12" />
        <Label Content="_Product key" Target="{Binding ElementName=KeyBox}" Padding="0,0,0,4" />
        <TextBox x:Name="KeyBox" />
        <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
            <Button Content="_Activate" IsDefault="True" Padding="12,4" Click="OnActivate" />
            <!-- Remove this button if the app has no free trial. -->
            <Button Content="Start free _trial" Padding="12,4" Margin="8,0,0,0" Click="OnStartTrial" />
        </StackPanel>
        <TextBlock x:Name="MessageText" TextWrapping="Wrap" Margin="0,12,0,0" />
    </StackPanel>
</Window>
```

and `EnterKeyWindow.xaml.cs` (remove `OnStartTrial` with the button):

```csharp
using System.Windows;
using RTFM.Licensing;

namespace Tidepool;

/// <summary>Shown instead of the app until it is licensed or in a trial.</summary>
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
        NotLicensedKind.NotActivated => "Enter your product key, or start a free trial.", // without a trial: "Enter your product key."
        NotLicensedKind.Expired => "Your license needs to be renewed online. Connect to the internet and reopen the app.",
        NotLicensedKind.TermEnded => "Your license has ended. Renew it to keep using the app.",
        NotLicensedKind.TrialEnded => "Your trial has ended. Enter a product key to keep using the app.",
        NotLicensedKind.Refused => "This key no longer works on this PC. Enter a product key.",
        NotLicensedKind.ClockRolledBack => "Your PC's date and time look wrong. Correct them in Settings, then reopen the app.",
        _ => "Your license needs to be activated again.",
    };
}
```

**Main window: License menu and status.** Add a **License** menu to the main window's existing menu (or add a `Menu`
docked at the top if it has none), and a status bar text showing the license. Keep the window's existing content:

```xml
<MenuItem Header="_License">
    <MenuItem Header="_Check now" Click="OnCheckNow" />
    <MenuItem Header="_Deactivate this PC…" Click="OnDeactivate" />
    <!-- Only with the self-service address from the developer's prompt: -->
    <Separator />
    <MenuItem Header="_Lost a PC? Free it online" Click="OnSelfService" />
</MenuItem>
```

```xml
<StatusBar DockPanel.Dock="Bottom">
    <TextBlock x:Name="LicenseText" />
</StatusBar>
```

Add to `MainWindow.xaml.cs`:

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

    // Asks for a key again if the license stops allowing use while the app is open.
    private void OnStatusChanged(object? sender, LicenseStatus status)
    {
        LicenseText.Text = Summary(status);
        if (status.AllowsUse || !IsVisible) return;
        if (new EnterKeyWindow(license) { Owner = this }.ShowDialog() != true) Close();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await license.CheckNowAsync();

    private async void OnDeactivate(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Deactivate this PC? You can then activate your key on another PC.\n\nDo this before you reset or replace this PC.",
            "Deactivate", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        try { await license.DeactivateAsync(); }
        catch (LicensingException error) { MessageBox.Show(this, LicenseMessages.Describe(error), "Deactivate"); }
    }

    // The self-service page's address from the developer's prompt; leave this out if it gave none.
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

`ForApp` throws only when `app-integration.json` is missing from the app's folder or malformed, which shows on the
first run.

**WinForms app:** create the `RTFMLicense` in the main form's `Load` handler (WinForms has installed its UI
synchronization context by then) and make the handler `async`: call `StartAsync()`, and when `!Status.AllowsUse`
show an enter-key form with `ShowDialog(this)`, closing the main form unless it returns `DialogResult.OK`. Use the
same `LicenseMessages`, a **License** item in a `MenuStrip`, a `StatusStrip` label, and the same `StatusChanged`
handler. Dispose the `RTFMLicense` in the main form's `FormClosed`.

**Features by edition** (only if the developer asks): signed custom fields are read from the license, never from
local settings:

```csharp
if (license.Status is LicenseStatus.Licensed { Info: var info } && info.Entitlement("feature.export") is EntitlementValue.Boolean { Value: true }) { … }
```

Values are `EntitlementValue.Text`, `Integer`, `Boolean` or `DateTime`. Ask the developer for the field codes; don't
guess them.

## Step 4. Check your work

- The project builds with no new warnings.
- `app-integration.json` is copied to the output folder (and installed by the installer), and unchanged.
- There is exactly one `RTFMLicense.ForApp(...)` call in the app, on the UI thread at startup.
- `StartAsync()` is called once, from the app's launch path.
- No product key appears anywhere in the code, logs, settings or tests.

Then tell the developer to test it:

1. Run the app, enter a test key from the License Manager (**New key**): the app opens; the status bar says
   "Licensed".
2. Close it, disconnect from the network, reopen: still licensed.
3. **License → Deactivate this PC**, then activate the same key again.

If activation fails with `unsupported_platform`, the developer adds Windows to the version's **Platforms** in the
License Manager (**Edit version**).

## API summary

| | |
|---|---|
| `RTFMLicense.ForApp(publisher, app, file = "app-integration.json")` | One per app, on the UI thread. License in `%LOCALAPPDATA%\<publisher>\<app>`. |
| `Status` | `LicenseStatus.Checking`, `Licensed(LicenseInfo)`, `Trial(TrialInfo)`, `NotLicensed(NotLicensedReason)`; `AllowsUse`. |
| `StatusChanged`, `PropertyChanged` | Raised on the UI thread when `Status` changes. |
| `StartAsync()` | At launch. Never throws for licensing failures, never blocks offline use. |
| `CheckNowAsync()` | Renew now. |
| `ActivateAsync(productKey, deviceLabel)` | Throws `LicensingException`. |
| `DeactivateAsync()` | Releases this PC's seat. |
| `StartTrialAsync()` | Starts or resumes a trial. |
| `LicenseInfo` | `LicenseId`, `Entitlements`, `Entitlement(code)`, `Term` (`Kind` `Fixed`/`Subscription`, `EndsAt`, `AccessEndsAt`), `InGrace`, `GraceUntil`, `IssuedAt`. |
| `TrialInfo` | `EndsAt`, `InGrace`, `Entitlements`. |
| `NotLicensedReason` | `Kind`: `NotActivated`, `Expired`, `TermEnded`, `TrialEnded`, `Refused` (with `Code`), `ClockRolledBack`, `InvalidStoredLicense`. |
| `LicensingException` | `Failure`: `Network`, `Service`, `Refused`, `Verification`, `Storage`, `NotActivated`, `NoTrial`; `Code`; `Message`. |

Full reference: https://github.com/jansenkarl/rtfm-licensing-dotnet/blob/main/docs/reference.md
