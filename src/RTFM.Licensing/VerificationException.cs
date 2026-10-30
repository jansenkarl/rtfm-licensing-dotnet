namespace RTFM.Licensing;

/// <summary>Why a manifest, lease or App integration file was not accepted. Every case fails closed.</summary>
public enum VerificationFailure
{
    /// <summary>Not in the expected shape.</summary>
    Malformed,
    /// <summary>The signature does not verify.</summary>
    UntrustedSignature,
    /// <summary>The JWS header is not the one expected.</summary>
    UnexpectedHeader,
    /// <summary>Issued for another issuer, environment, tenant, product, version or installation.</summary>
    WrongScope,
    /// <summary>Signed by a key the manifest does not list.</summary>
    UnknownKey,
    /// <summary>Signed by a key the manifest lists as not active.</summary>
    InactiveKey,
    /// <summary>Older than a lease already accepted (a replay or a rollback).</summary>
    StaleState,
    /// <summary>Timing values out of order.</summary>
    InvalidTiming,
}

/// <summary>A manifest, lease or App integration file that did not verify.</summary>
public sealed class VerificationException : Exception
{
    /// <summary>What failed.</summary>
    public VerificationFailure Failure { get; }

    /// <summary>The field or key involved, if any.</summary>
    public string Detail { get; }

    internal VerificationException(VerificationFailure failure, string detail = "")
        : base(detail.Length == 0 ? failure.ToString() : $"{failure}: {detail}")
    {
        Failure = failure;
        Detail = detail;
    }

    internal static VerificationException Malformed(string detail) => new(VerificationFailure.Malformed, detail);
    internal static VerificationException WrongScope(string detail) => new(VerificationFailure.WrongScope, detail);
}
