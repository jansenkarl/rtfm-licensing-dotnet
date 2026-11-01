# Changelog

Versions follow [Semantic Versioning](https://semver.org). Before 1.0.0, a minor version (0.x.0) may change the
public API; a patch version (0.x.y) does not.

## 0.1.0 (2026-11-01)

First version, ported from the Swift package 0.1.0 and tested against the RTFM licensing staging service on Windows 11
(x64 and Arm64).

- `RTFMLicense`: activation, quiet renewal at launch, `CheckNowAsync()`, deactivation and trials, with a `Status`
  (`Licensed`, `Trial`, `NotLicensed(reason)`) and `StatusChanged` and `PropertyChanged` on the UI thread.
- Leases verified against the Product's root-signed key list, with signing-key rotation, scope checks, high-water
  marks per activation, license and trial, typed entitlements, and the term of fixed-term and subscription keys.
- Offline use until the lease's grace period ends; only network and service failures allow grace.
- The installation key in the TPM when the PC has one, otherwise in software; one state file per app and Windows
  user, encrypted with DPAPI and replaced atomically. Requests that can use a seat are journaled and replayed exactly
  after a lost response.
- Command-line and WPF samples.
