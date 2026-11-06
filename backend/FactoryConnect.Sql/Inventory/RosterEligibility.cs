namespace FactoryConnect.Sql.Inventory;

/// <summary>
/// A fail-closed eligibility decision over a single, coherent, already-authorized
/// authority cut. This does not obtain the cut, verify authorization or publish an API.
/// A missing proof is never interpreted as a healthy empty roster.
/// </summary>
public static class RosterEligibility
{
    public static RosterDecision Evaluate(MachineRosterEvidence evidence, DateTimeOffset evaluationTime)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.MachineId == Guid.Empty || evidence.SiteId == Guid.Empty)
            return new(RosterStatus.Unavailable, "InvalidIdentity");
        if (!evidence.AuthorityCutVerified || !evidence.AuthorizationVerified ||
            !evidence.PolicyRevocationsVerified || !evidence.RuntimeAssignmentVerified)
            return new(RosterStatus.Unavailable, "UnverifiedAuthority");
        if (evidence.MachineRetired || evidence.SiteRetired)
            return new(RosterStatus.Excluded, "Retired");
        if (evidence.Disabled || evidence.Suspended)
            return new(RosterStatus.Excluded, "DisabledOrSuspended");
        if (evidence.PackageRevisionId is null || evidence.RuntimeIdentityId is null ||
            evidence.PackageRevisionId == Guid.Empty || evidence.RuntimeIdentityId == Guid.Empty)
            return new(RosterStatus.Excluded, "NoAcknowledgedConfiguration");
        if (evidence.EffectiveFrom > evaluationTime ||
            (evidence.EffectiveUntil is not null && evaluationTime >= evidence.EffectiveUntil.Value))
            return new(RosterStatus.Excluded, "OutsideEffectiveInterval");
        if (evidence.PolicyRevoked || evidence.RuntimeCredentialRevoked)
            return new(RosterStatus.Excluded, "Revoked");
        if (!evidence.ExactActivationAcknowledged)
            return new(RosterStatus.Excluded, "ActivationNotAcknowledged");
        return new(RosterStatus.Eligible, "EligibleConfigurationNotConnectivity");
    }
}

public enum RosterStatus { Unavailable, Excluded, Eligible }
public sealed record RosterDecision(RosterStatus Status, string Reason);

/// <summary>
/// Caller-supplied facts must be assembled within a verified consistent security,
/// policy and machine-activation read. Boolean proofs are not themselves a trust boundary.
/// </summary>
public sealed record MachineRosterEvidence(
    Guid MachineId,
    Guid SiteId,
    bool AuthorityCutVerified,
    bool AuthorizationVerified,
    bool PolicyRevocationsVerified,
    bool RuntimeAssignmentVerified,
    bool MachineRetired,
    bool SiteRetired,
    bool Disabled,
    bool Suspended,
    Guid? PackageRevisionId,
    Guid? RuntimeIdentityId,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveUntil,
    bool PolicyRevoked,
    bool RuntimeCredentialRevoked,
    bool ExactActivationAcknowledged);
