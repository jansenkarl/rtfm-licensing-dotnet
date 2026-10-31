# RTFM Licensing for .NET

.NET package for adding RTFM License Manager licensing to a Windows app: product key activation, offline use with
signed leases, quiet renewal, deactivation, trials, and fixed-term or subscription keys.

**Version 0.1.0.** MIT licensed (see `LICENSE`). Changes are listed in `CHANGELOG.md`.

**Docs:** [Getting started](docs/getting-started.md)

- .NET 8 or later, on Windows 10 or 11 (x64 or Arm64). Tested with WPF; WinForms and WinUI use the same calls. The
  only dependency is Microsoft's `System.Security.Cryptography.ProtectedData`.
- No hardware fingerprinting: each installation has its own key, kept in the PC's TPM when it has one.
- Works offline: a lease lasts weeks, and if the service can't be reached when it runs out, a grace period follows.

## Quickstart

1. In the License Manager, open your Product Version and choose **App integration**, then download the file. Add it
   to your project as `app-integration.json` with **Copy to Output Directory** set to **Copy if newer**. It holds only
   public values.
2. Add the package:
   ```
   dotnet add package RTFMDesign.Licensing
   ```
3. Create one `RTFMLicense` for the app, start it at launch, and ask for a key when it doesn't allow use:

```csharp
using RTFM.Licensing;

// Reads app-integration.json beside the app; keeps the license in %LOCALAPPDATA%\Northwind\Tidepool.
var license = RTFMLicense.ForApp("Northwind", "Tidepool");
await license.StartAsync();
if (!license.Status.AllowsUse)
{
    // Show your enter-key window. On Activate:
    try { await license.ActivateAsync(productKey); }
    catch (LicensingException error) when (error.Failure is LicensingFailure.Refused or LicensingFailure.Network)
    {
        ShowMessage(error.Message); // written for customers
    }
}
```

`license.Status` is one of:

| Status | Meaning |
|---|---|
| `LicenseStatus.Licensed { Info: var info }` | A key is active. `info.Term` is set for fixed-term and subscription keys ("Renews …" or "Ends …"); `info.InGrace` means the lease ran out offline and use continues until `info.GraceUntil`; `info.Entitlement("code")` reads signed entitlements, including your custom license fields. |
| `LicenseStatus.Trial { Info: var info }` | A trial runs until `info.EndsAt`. Start one with `license.StartTrialAsync()`. |
| `LicenseStatus.NotLicensed { Reason: var reason }` | Ask for a key. `reason.Kind` says why: `NotActivated`, `Expired`, `TermEnded`, `TrialEnded`, `Refused` (with `reason.Code`), `ClockRolledBack`, `InvalidStoredLicense`. |
| `LicenseStatus.Checking` | Before `StartAsync()` finishes. |

`RTFMLicense` raises `StatusChanged` and `PropertyChanged` on the UI thread it was created on, so you can bind to
`Status` or react to it directly.

Offer **Deactivate** (`license.DeactivateAsync()`) so a customer can move their key to another PC.
`license.CheckNowAsync()` renews the lease immediately, for a "Check now" button; `StartAsync()` renews only when the
lease is due.

The full walkthrough for a WPF app, with an enter-key window and a License menu with **Deactivate this PC**, is in
[Getting started](docs/getting-started.md). A working WPF app is in `samples/WpfSample`.

## Try it from the command line

The command-line sample makes the same calls against a real licensing service, using your App integration file. It
reads the product key from `RTFM_PRODUCT_KEY`, so the key stays out of your shell history:

```powershell
$json = "$HOME\Downloads\app-integration-my-app-1-staging.json"
$env:RTFM_PRODUCT_KEY = [Net.NetworkCredential]::new('', (Read-Host 'Product key' -AsSecureString)).Password
dotnet run --project samples\LicenseSample -- $json activate
Remove-Item Env:RTFM_PRODUCT_KEY
dotnet run --project samples\LicenseSample -- $json status
dotnet run --project samples\LicenseSample -- $json deactivate
```

Or try the WPF sample: copy the file to `samples\WpfSample\app-integration.json` and run
`dotnet run --project samples\WpfSample`.

## How it works

- At first activation or trial, the SDK creates a P-256 key for this installation. On Windows it is a non-exportable
  key in the TPM ("Microsoft Platform Crypto Provider") when the PC has one, otherwise a software key.
- Everything the SDK keeps is one file, `%LOCALAPPDATA%\<publisher>\<app>\license-state.dat`, encrypted for the
  Windows user with DPAPI and replaced atomically. Each Windows user on a PC has their own license and uses their own
  seat.
- Activation sends the product key with a signed one-time challenge. The service answers with a signed lease bound to
  this installation. The SDK checks it against your Product's key list, which is signed by the root key pinned in
  `app-integration.json`.
- Every request that can use a seat is saved before it is sent and repeated exactly if the answer is lost, so a
  dropped connection never uses a second seat.
- Leases renew quietly once due. Only a network or service failure allows the grace period; a revoked or ended key
  stops at its next renewal.

## Status

0.1.0 was tested against the RTFM licensing staging service on a Windows 11 Arm64 VM and a Windows 11 x64 PC, both
with a TPM: activation, status at launch, renewal, deactivation and a trial, from the command-line and WPF samples.
Its 38 tests, ported from the Swift package (signed lease fixtures, key rotation, the request journal and the
conformance cases), pass on Windows and macOS.
