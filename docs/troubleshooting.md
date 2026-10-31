# Troubleshooting

## The app fails at launch in `RTFMLicense.ForApp`

`app-integration.json` is not next to the app's executable, or it was edited. Check that the file's **Copy to Output
Directory** is **Copy if newer** and that your installer includes it, and download it again from **App integration**
if in doubt. The error names the problem, for example `app-integration.json is not in the app's folder` or
`App integration file is missing productCode`.

## Every activation fails with "Couldn't reach the licensing service"

- A firewall, proxy or network filter blocks the address in `apiBaseUrl`. The SDK uses the system proxy settings.
- The PC is offline. Activation needs the network once; after that the app works offline.

## `unsupported_platform`

The version's **Platforms** don't include Windows. In the License Manager, open the version's **Edit version** and add
Windows. Adding a platform is always allowed; existing Mac keys then also activate on Windows.

## `invalid_product_key`

- The key was mistyped. Case, spaces and dashes don't matter, but every letter and digit does.
- The key was revoked, or the version is not enabled.
- The key is for a different product or version than the App integration file in the app. A key belongs to one
  version: a key for version 1 does not activate an app built with version 2's file.

## `activation_seat_limit_reached`

The key is active on as many devices as it covers. The customer deactivates it on one of them (your app's
**Deactivate this PC**). If the customer no longer has that PC, they can free it on the self-service page, which the
refusal message names (`https://lm.rtfmdesign.com/deactivate/<product code>`). Or open the key in the License Manager
and choose **Deactivate** on that device's row under Activations. Its seat is free at once, and the customer activates
the new PC with the same key. The old PC stops when it next renews its license. This does not use one of the key's
deactivations.

## `deactivation_limit_reached`

The key has been moved as many times as its version allows. The limit is set with the version's **Edit** action. You
can still free a PC yourself with **Deactivate** on the key's page in the License Manager; that does not count
against the limit.

## The license is gone after resetting Windows or clearing the TPM

The installation's key was in the TPM or in the user's profile, so a reset removes it and the PC counts as a new
device. The customer frees the old activation on the self-service page, then activates again. Uninstalling and
reinstalling the app does not remove the license: it lives in `%LOCALAPPDATA%`, not in the app's folder.

## Another Windows user on the same PC isn't licensed

Each Windows user has their own license, encrypted for that user, and uses a seat of their own. They activate with
the same key if it has a seat free.

## Start over on a test PC

Deactivate first, so the seat is released. Then delete `%LOCALAPPDATA%\<publisher>\<app>\license-state.dat` (the names
passed to `ForApp`). The samples keep theirs under `%LOCALAPPDATA%\RTFM Sample`. The TPM key the installation used
stays in Windows key storage, unused.

## `Status` is `NotLicensed` with `ClockRolledBack`

The PC's clock is more than 5 minutes earlier than a time the app has already seen. Turn on **Set time automatically**
in **Settings → Time & language → Date & time**, then reopen the app.

## `Status` is `NotLicensed` with `Expired`

The PC was offline longer than the lease and its grace period allow. Going online and reopening the app renews it.

## `Status` is `NotLicensed` with `Refused` at launch

The service refused the key at renewal: it was revoked, deactivated from another PC or the License Manager, or the
license ended. The stored license has been removed; activating a valid key works again.

## `LicensingFailure.Verification`

A response did not verify against the App integration file. Retrying will not help. Check that the app ships the file
for the same environment as the service it talks to, downloaded from the right version.

## `LicensingFailure.Storage`

The state file could not be read, written or decrypted. DPAPI ties it to the Windows user, so a file copied from
another user or PC can't be read; delete it and activate again (after freeing the old activation if needed).

## `dotnet test` says "You must install or update .NET"

The tests target .NET 8. With only a newer SDK installed, add the .NET 8 runtime:
`winget install --id Microsoft.DotNet.Runtime.8 -e`.

## Still stuck

Open an issue at https://github.com/jansenkarl/rtfm-licensing-dotnet/issues with the package version, the Windows
version, the error code, and what you did. **Never include a product key**, a customer's email or the license state
file.
