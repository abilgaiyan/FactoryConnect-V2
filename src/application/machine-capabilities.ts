export interface MachineCapability {
    readonly key: string;
    readonly support: 'supported' | 'unsupported' | 'not-assessed';
    readonly qualification: 'qualified' | 'pending' | 'rejected' | 'not-assessed';
}
export interface MachineProfile {
    readonly machineId: string;
    readonly profileId: string;
    readonly profileVersion: string;
    readonly integrationCategoryId: string;
    readonly approvalReference: string;
    readonly effectiveFrom: string;
    readonly faultLabel?: 'Alarm' | null;
    readonly stoppedLabel: 'Machine OFF' | null;
    readonly capabilities: readonly MachineCapability[];
}
export function validProfiles(version: unknown, value: unknown, machineIds: readonly string[]): boolean {
    if (version === undefined && value === undefined) return true;
    if (version !== 1 || !Array.isArray(value)) return false;
    const seen = new Set<string>();
    return value.every(p => {
        if (!p || typeof p !== 'object' || typeof p.machineId !== 'string') return false;
        const id = p.machineId.toLowerCase();
        if (seen.has(id) || !machineIds.includes(id)) return false;
        seen.add(id);
        if (![p.profileId, p.profileVersion, p.integrationCategoryId, p.approvalReference, p.effectiveFrom].every(v => typeof v === 'string' && v.trim().length > 0)
            || !Number.isFinite(Date.parse(p.effectiveFrom)) || ![undefined, null, 'Alarm'].includes(p.faultLabel) || ![null, 'Machine OFF'].includes(p.stoppedLabel) || !Array.isArray(p.capabilities)) return false;
        const keys = new Set<string>();
        return p.capabilities.every((c: MachineCapability) => {
            if (!c || typeof c.key !== 'string' || !c.key.trim() || keys.has(c.key)) return false;
            keys.add(c.key);
            return ['supported', 'unsupported', 'not-assessed'].includes(c.support)
                && ['qualified', 'pending', 'rejected', 'not-assessed'].includes(c.qualification)
                && (c.qualification !== 'qualified' || c.support === 'supported');
        }) && ((p.stoppedLabel === null && (p.faultLabel === undefined || p.faultLabel === null)) || p.capabilities.some((c: MachineCapability) => c.key === 'operational-state' && c.support === 'supported' && c.qualification === 'qualified'));
    });
}
export function profileFor(profiles: readonly MachineProfile[] | undefined, machineId: string): MachineProfile | undefined {
    return profiles?.find(p => p.machineId.toLowerCase() === machineId.toLowerCase());
}
