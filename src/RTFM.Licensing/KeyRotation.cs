namespace RTFM.Licensing;

/// <summary>The signing-key rotation rule: trust only active keys in the newest root-signed manifest held; on an
/// unlisted <c>kid</c>, fetch the manifest once more (at most once per <c>kid</c> per hour); never replace the held
/// manifest with an older one.</summary>
public static class KeyRotation
{
    /// <summary>The shortest time between two fetches for the same unlisted <c>kid</c>.</summary>
    public static readonly TimeSpan RefetchInterval = TimeSpan.FromHours(1);

    /// <summary>What to keep and whether the lease's key can be used.</summary>
    /// <param name="Manifest">The manifest to keep (and store) afterwards.</param>
    /// <param name="Key">The active key for the lease's <c>kid</c>, or null when the lease must not be accepted.</param>
    /// <param name="Fetched">Whether a fetch was attempted.</param>
    public sealed record Outcome(VerificationManifest Manifest, ManifestKey? Key, bool Fetched);

    /// <summary><paramref name="fetch"/> returns a manifest already verified against the pinned root, or throws.</summary>
    public static async Task<Outcome> ResolveAsync(string kid, VerificationManifest held, DateTimeOffset? lastRefetchForKid,
        DateTimeOffset now, Func<Task<VerificationManifest>> fetch)
    {
        if (held.ListedKey(kid) is { } listed) return new Outcome(held, listed.Status == "active" ? listed : null, false);
        if (lastRefetchForKid is { } last && now - last < RefetchInterval) return new Outcome(held, null, false);
        VerificationManifest fresh;
        try { fresh = await fetch().ConfigureAwait(false); }
        catch (Exception) { return new Outcome(held, null, true); }
        if (fresh.GeneratedAt < held.GeneratedAt) return new Outcome(held, null, true);
        var key = fresh.ListedKey(kid);
        return new Outcome(fresh, key?.Status == "active" ? key : null, true);
    }
}
