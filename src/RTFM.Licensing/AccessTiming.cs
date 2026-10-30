namespace RTFM.Licensing;

/// <summary>What happened when the app last tried to reach the licensing service.</summary>
public enum NetworkState
{
    /// <summary>No refresh was attempted, or none was needed.</summary>
    NotChecked,
    /// <summary>The service answered (with or without a new lease).</summary>
    Available,
    /// <summary>A network or service failure: the only case that allows grace after expiry.</summary>
    Unavailable,
}

/// <summary>Whether a lease grants access at a time.</summary>
public enum AccessDecision
{
    /// <summary>Within the lease.</summary>
    Valid,
    /// <summary>Expired, but within signed grace because the service could not be reached.</summary>
    Grace,
    /// <summary>Issued in the future.</summary>
    NotYetValid,
    /// <summary>The clock is earlier than a time the app has already observed.</summary>
    ClockRolledBack,
    /// <summary>Expired.</summary>
    Expired,
    /// <summary>The trial has ended.</summary>
    TrialEnded,
}

/// <summary>Whether a verified lease grants access at a given time.</summary>
public static class AccessTiming
{
    /// <summary>The decision for <paramref name="lease"/> at <paramref name="now"/> (Unix seconds).</summary>
    public static AccessDecision Decide(Lease lease, long now, long observedTime, NetworkState network)
    {
        if (lease.IssuedAt > now || lease.NotBefore > now) return AccessDecision.NotYetValid;
        if (observedTime > now) return AccessDecision.ClockRolledBack;
        if (lease.Kind is LeaseKind.Trial trial && now >= trial.EndsAt.ToUnixTimeSeconds()) return AccessDecision.TrialEnded;
        if (now <= lease.ExpiresAt) return AccessDecision.Valid;
        if (now <= lease.GraceUntil && network == NetworkState.Unavailable) return AccessDecision.Grace;
        return AccessDecision.Expired;
    }
}
