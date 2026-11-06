using FactoryConnect.Sql.Inventory;

internal static class RosterEligibilityAcceptance
{
    public static int PureChecks()
    {
        var count = 0;
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var baseline = new MachineRosterEvidence(
            Guid.NewGuid(), Guid.NewGuid(), true, true, true, true,
            false, false, false, false, Guid.NewGuid(), Guid.NewGuid(),
            now.AddHours(-1), now.AddHours(1), false, false, true);
        void Expect(MachineRosterEvidence item, RosterStatus expected, string reason)
        {
            var actual = RosterEligibility.Evaluate(item, now);
            if (actual.Status != expected || actual.Reason != reason)
                throw new InvalidOperationException($"Roster {reason}: got {actual}");
            count++;
        }
        Expect(baseline, RosterStatus.Eligible, "EligibleConfigurationNotConnectivity");
        Expect(baseline with { AuthorityCutVerified = false }, RosterStatus.Unavailable, "UnverifiedAuthority");
        Expect(baseline with { AuthorizationVerified = false }, RosterStatus.Unavailable, "UnverifiedAuthority");
        Expect(baseline with { PolicyRevocationsVerified = false }, RosterStatus.Unavailable, "UnverifiedAuthority");
        Expect(baseline with { RuntimeAssignmentVerified = false }, RosterStatus.Unavailable, "UnverifiedAuthority");
        Expect(baseline with { MachineId = Guid.Empty }, RosterStatus.Unavailable, "InvalidIdentity");
        Expect(baseline with { SiteRetired = true }, RosterStatus.Excluded, "Retired");
        Expect(baseline with { MachineRetired = true }, RosterStatus.Excluded, "Retired");
        Expect(baseline with { Disabled = true }, RosterStatus.Excluded, "DisabledOrSuspended");
        Expect(baseline with { Suspended = true }, RosterStatus.Excluded, "DisabledOrSuspended");
        Expect(baseline with { PackageRevisionId = null }, RosterStatus.Excluded, "NoAcknowledgedConfiguration");
        Expect(baseline with { RuntimeIdentityId = null }, RosterStatus.Excluded, "NoAcknowledgedConfiguration");
        Expect(baseline with { ExactActivationAcknowledged = false }, RosterStatus.Excluded, "ActivationNotAcknowledged");
        Expect(baseline with { PolicyRevoked = true }, RosterStatus.Excluded, "Revoked");
        Expect(baseline with { RuntimeCredentialRevoked = true }, RosterStatus.Excluded, "Revoked");
        Expect(baseline with { EffectiveFrom = now }, RosterStatus.Eligible, "EligibleConfigurationNotConnectivity");
        Expect(baseline with { EffectiveFrom = now.AddTicks(1) }, RosterStatus.Excluded, "OutsideEffectiveInterval");
        Expect(baseline with { EffectiveUntil = now }, RosterStatus.Excluded, "OutsideEffectiveInterval");
        Expect(baseline with { EffectiveUntil = now.AddTicks(1) }, RosterStatus.Eligible, "EligibleConfigurationNotConnectivity");
        Expect(baseline with { EffectiveUntil = null }, RosterStatus.Eligible, "EligibleConfigurationNotConnectivity");
        Console.WriteLine($"PASS: {count} fail-closed roster eligibility fixtures");
        return count;
    }
}
