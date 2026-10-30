// A command-line sample and test tool for the RTFM.Licensing package.
//   dotnet run --project samples/LicenseSample -- <app-integration.json> status|check|activate|deactivate|trial
// `activate` reads the product key from RTFM_PRODUCT_KEY, so it stays out of shell history. The license state is kept
// in this user's local application data under "RTFM Sample\<product version code>".
using RTFM.Licensing;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: LicenseSample <app-integration.json> status|check|activate|deactivate|trial");
    return 2;
}
var integration = AppIntegration.FromFile(args[0]);
var store = FileLicenseStore.ForApp("RTFM Sample", integration.ProductVersionCode);
using var license = new RTFMLicense(integration, store, appVersion: "0.1.0");
try
{
    switch (args[1])
    {
        case "status": await license.StartAsync(); break;
        case "check": await license.CheckNowAsync(); break;
        case "activate":
            var key = Environment.GetEnvironmentVariable("RTFM_PRODUCT_KEY") ?? throw new InvalidOperationException("Set RTFM_PRODUCT_KEY.");
            await license.ActivateAsync(key, deviceLabel: Environment.MachineName);
            break;
        case "deactivate": await license.StartAsync(); await license.DeactivateAsync(); break;
        case "trial": await license.StartTrialAsync(); break;
        default: Console.Error.WriteLine($"Unknown command: {args[1]}"); return 2;
    }
}
catch (LicensingException error)
{
    Console.Error.WriteLine($"FAIL {error.Failure}{(error.Code is null ? "" : $" ({error.Code})")}: {error.Message}");
    return 1;
}
Console.WriteLine($"Platform: {(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux")}; state: {store.Path}");
Console.WriteLine(license.Status switch
{
    LicenseStatus.Licensed { Info: var info } => $"Licensed{(info.InGrace ? " (offline grace)" : "")}" +
        $"{(info.Term is { } term ? $"; {(term.Kind == TermKind.Subscription ? "renews" : "ends")} {term.EndsAt:yyyy-MM-dd}" : "")}" +
        $"; last confirmed {info.IssuedAt:yyyy-MM-dd HH:mm} UTC; {info.Entitlements.Count} entitlements",
    LicenseStatus.Trial { Info: var trial } => $"Trial until {trial.EndsAt:yyyy-MM-dd}",
    LicenseStatus.NotLicensed { Reason: var reason } => $"Not licensed: {reason.Kind}{(reason.Code is null ? "" : $" ({reason.Code})")}",
    _ => "Checking",
});
return license.Status.AllowsUse || args[1] == "deactivate" ? 0 : 1;
