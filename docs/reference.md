# API reference

Version 0.1.0, namespace `RTFM.Licensing`. Everything an app needs is `RTFMLicense` and the types it returns. The
lower-level types are listed at the end for tests and tools.

## RTFMLicense

`public sealed class RTFMLicense : INotifyPropertyChanged, IDisposable`. Create one per app, on the UI thread, and
keep it for the app's lifetime.

| | |
|---|---|
| `static ForApp(string publisher, string app, string file = "app-integration.json")` | Reads `file` from the app's folder (`AppContext.BaseDirectory`) and keeps the license in `%LOCALAPPDATA%\<publisher>\<app>\license-state.dat`, encrypted with DPAPI for the Windows user. The app version comes from the entry assembly. Throws `FileNotFoundException` if the file is missing and `VerificationException` if it is invalid. |
| `RTFMLicense(AppIntegration integration, ILicenseStore store, string appVersion, HttpClient? http = null, Func<DateTimeOffset>? now = null, bool preferTpm = true)` | For tests and tools: pass an `AppIntegration`, an `ILicenseStore` (for example `new MemoryLicenseStore()`), the app version, and optionally an `HttpClient` and a clock. `preferTpm: false` always uses a software key. |
| `Status` | The current `LicenseStatus`. `Checking` until `StartAsync()` finishes. |
| `StatusChanged` | `EventHandler<LicenseStatus>`, raised when `Status` changes, on the synchronization context the object was created on. |
| `PropertyChanged` | Raised for `Status`, on the same context, for data binding. |
| `Integration` | The parsed App integration file. |
| `StartAsync(CancellationToken cancel = default)` | Call once at launch. Finishes a request left unfinished by an earlier launch, renews the lease if it is due, and updates `Status`. Never throws for licensing failures and never blocks offline use. |
| `CheckNowAsync(CancellationToken cancel = default)` | Renews now, even if not due. For a "Check now" button. |
| `ActivateAsync(string productKey, string? deviceLabel = null, CancellationToken cancel = default)` | Activates a key on this PC. `deviceLabel` is an optional name for this PC, shown on the key's page in the License Manager and on the self-service page. |
| `DeactivateAsync(CancellationToken cancel = default)` | Releases this PC's seat so the key can be activated on another PC. |
| `StartTrialAsync(CancellationToken cancel = default)` | Starts a trial, or resumes this PC's trial if it already has one. |
| `Dispose()` | Releases the HTTP client it created; one passed in stays yours. |

The three methods that change the license throw `LicensingException`. A request that can use a seat is saved before
it is sent; if the answer is lost, the next call (or the next `StartAsync()`) repeats it exactly, so a lost connection
never uses a second seat. Requests time out after 30 seconds (unless you pass your own `HttpClient`).

## LicenseStatus

```csharp
public abstract record LicenseStatus
{
    public sealed record Checking : LicenseStatus;
    public sealed record Licensed(LicenseInfo Info) : LicenseStatus;
    public sealed record Trial(TrialInfo Info) : LicenseStatus;
    public sealed record NotLicensed(NotLicensedReason Reason) : LicenseStatus;
    public bool AllowsUse { get; } // true for Licensed and Trial
}
```

### LicenseInfo

| | |
|---|---|
| `LicenseId` | The license's ID. |
| `Entitlements` | `IReadOnlyList<Entitlement>`: signed values, `activation.limit` and your custom fields. |
| `Entitlement(string code)` | One value by code, or null. |
| `Term` | Set for fixed-term and subscription keys; null for perpetual keys. |
| `InGrace` | The lease ran out and the service could not be reached; use continues until `GraceUntil`. |
| `GraceUntil` | The last moment this lease allows use without renewing. |
| `IssuedAt` | When the service last confirmed this key. |

### Term

`public sealed record Term(TermKind Kind, DateTimeOffset EndsAt, DateTimeOffset AccessEndsAt)`

| | |
|---|---|
| `Kind` | `TermKind.Fixed` or `TermKind.Subscription`. |
| `EndsAt` | The end of the term, or of the paid subscription period. Show "Ends …" or "Renews …". |
| `AccessEndsAt` | When use stops: `EndsAt` plus the version's term grace days, if any. After it, `Status` is `NotLicensed` with `TermEnded`. |

A subscription that renews moves `EndsAt` forward at the next lease renewal.

### TrialInfo

`public sealed record TrialInfo(DateTimeOffset EndsAt, bool InGrace, IReadOnlyList<Entitlement> Entitlements)`:
when the trial ends, whether it is in grace (as for `LicenseInfo`), and its signed values.

### NotLicensedReason

`public sealed record NotLicensedReason(NotLicensedKind Kind, string? Code = null)`; `Code` is set for `Refused`.

| `NotLicensedKind` | Meaning | What to tell the customer |
|---|---|---|
| `NotActivated` | No key and no trial on this PC. | Ask for a key. |
| `Expired` | The lease and its grace period ran out without reaching the service. | Go online and reopen the app. |
| `TermEnded` | A fixed term or subscription has ended. | Renew. |
| `TrialEnded` | The trial has ended. | Buy a key. |
| `Refused` | The service refused the key at renewal: revoked, deactivated from another PC or the License Manager, or no longer valid. The stored license has been removed. | Enter a key. |
| `ClockRolledBack` | The PC's clock is more than 5 minutes earlier than a time the app has already seen. | Correct the date and time. |
| `InvalidStoredLicense` | The stored license did not verify. | Activate again. |

## Entitlement

```csharp
public sealed record Entitlement(string Code, EntitlementValue Value);

public abstract record EntitlementValue
{
    public sealed record Text(string Value) : EntitlementValue;
    public sealed record Integer(long Value) : EntitlementValue;
    public sealed record Boolean(bool Value) : EntitlementValue;
    public sealed record DateTime(DateTimeOffset Value) : EntitlementValue;
}
```

Custom field codes are lowercase: 2 to 64 of `a-z`, `0-9`, `_`, `.` and `-`, starting with a letter.

## LicensingException

| | |
|---|---|
| `Failure` | A `LicensingFailure`, below. |
| `Code` | The service's error code, for `Service` and `Refused`. |
| `Message` | For `Refused`, written for customers and safe to show. |
| `AllowsGrace` | True for `Network` and `Service`: only those let an expired lease keep working through its grace period. A refusal never does. |
| `Verification` | The `VerificationException`, for `Verification`. |

| `LicensingFailure` | Meaning |
|---|---|
| `Network` | The service could not be reached. |
| `Service` | The service failed or asked to retry later. Treated like `Network`. |
| `Refused` | The service refused for good. Common codes are below. |
| `Verification` | A response did not verify. Retrying will not help; check that the App integration file is the right one. |
| `Storage` | The license state file could not be read or written. |
| `NotActivated` | `DeactivateAsync()` or a renewal was called with no activation. |
| `NoTrial` | A trial operation was called with no trial. |

### Refusal codes

| Code | When |
|---|---|
| `invalid_product_key` | The key is mistyped, revoked or inactive, is for another product or version than the App integration file names, or that version is not enabled. |
| `activation_seat_limit_reached` | The key is active on as many devices as it covers. The customer deactivates one of them first, in the app or on the self-service page the message names. |
| `deactivation_limit_reached` | The key has been moved as many times as its version allows. |
| `license_term_ended` | The fixed term or subscription has ended. |
| `invalid_activation_credentials` | At renewal or deactivation: this PC's activation was deactivated elsewhere or the key was revoked. |
| `unsupported_platform` | The version's **Platforms** don't include Windows. |
| `unsupported_product` | The product or version is not enabled for licensing or trials. |
| `trials_disabled` | Trials are not available. |
| `trial_converted` | This PC's trial became a paid license. |

## Lower-level types

For tests, tools and custom storage. Apps don't need them.

| Type | Use |
|---|---|
| `AppIntegration(ReadOnlySpan<byte> json)`, `AppIntegration.FromFile(path)` | Parses an App integration file (`rtfm.app-integration.v1`). Throws `VerificationException`. |
| `ILicenseStore` | `Load()`, `Commit(LicenseState)` (atomic), `Erase()`. |
| `FileLicenseStore(path)`, `FileLicenseStore.ForApp(publisher, app)` | The default store: one file, encrypted with DPAPI on Windows, readable only by the user elsewhere. `Path` is the file. |
| `MemoryLicenseStore()` | An in-memory store for tests. |
| `LicensingClient` | The client behind `RTFMLicense`, returning verified `Lease` values. |
| `StoreException`, `VerificationException` | Storage and verification failures, wrapped in `LicensingException` by the client. |

## Platforms

On Windows the installation key is a non-exportable P-256 key in the TPM ("Microsoft Platform Crypto Provider") when
the PC has a usable one, otherwise a software key kept in the encrypted state file. The package also runs on macOS
and Linux with a software key, which is useful for tests; the version's Platforms must include the platform the app
reports (`windows`, `macos` or `linux`).

## Versions

Versions follow [Semantic Versioning](https://semver.org). Before 1.0.0 a minor version (0.x.0) may change the public
API and a patch version (0.x.y) does not, so depend on `0.1.*` (for example `Version="0.1.*"` in a
`PackageReference`). Changes are in [CHANGELOG.md](../CHANGELOG.md).
