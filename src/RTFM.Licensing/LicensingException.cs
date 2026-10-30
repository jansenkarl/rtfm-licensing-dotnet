namespace RTFM.Licensing;

/// <summary>Why an online licensing operation did not complete.</summary>
public enum LicensingFailure
{
    /// <summary>The licensing service could not be reached. Offline use continues within signed grace.</summary>
    Network,
    /// <summary>The service failed or asked to retry later. Treated like a network failure for grace.</summary>
    Service,
    /// <summary>The service refused the request for good, for example <c>activation_seat_limit_reached</c>,
    /// <c>license_revoked</c>, <c>license_term_ended</c> or <c>invalid_product_key</c>.</summary>
    Refused,
    /// <summary>A response or stored lease did not verify.</summary>
    Verification,
    /// <summary>The license state could not be saved or read.</summary>
    Storage,
    /// <summary>The operation needs an activation this installation does not have.</summary>
    NotActivated,
    /// <summary>The operation needs a trial this installation does not have.</summary>
    NoTrial,
}

/// <summary>An online licensing operation that did not complete. For <see cref="LicensingFailure.Refused"/>,
/// <see cref="Exception.Message"/> is safe to show the customer.</summary>
public sealed class LicensingException : Exception
{
    /// <summary>What kind of failure.</summary>
    public LicensingFailure Failure { get; }

    /// <summary>The service's error code, for <see cref="LicensingFailure.Service"/> and <see cref="LicensingFailure.Refused"/>.</summary>
    public string? Code { get; }

    /// <summary>The verification failure, for <see cref="LicensingFailure.Verification"/>.</summary>
    public VerificationException? Verification { get; }

    internal LicensingException(LicensingFailure failure, string message, string? code = null, VerificationException? verification = null, Exception? inner = null)
        : base(message, inner ?? verification)
    {
        Failure = failure;
        Code = code;
        Verification = verification;
    }

    /// <summary>Network and service failures are the only ones that allow grace after a lease expires.</summary>
    public bool AllowsGrace => Failure is LicensingFailure.Network or LicensingFailure.Service;

    internal static LicensingException Network(Exception? inner = null) =>
        new(LicensingFailure.Network, "Couldn't reach the licensing service. Check your connection and try again.", inner: inner);
    internal static LicensingException Service(string code) => new(LicensingFailure.Service, $"The licensing service failed ({code}). Try again later.", code);
    internal static LicensingException Refused(string code, string message) => new(LicensingFailure.Refused, message, code);
    internal static LicensingException Verify(VerificationException error) => new(LicensingFailure.Verification, error.Message, verification: error);
    internal static LicensingException Storage(string detail, Exception? inner = null) => new(LicensingFailure.Storage, $"The license could not be saved or read: {detail}", inner: inner);
}
