// Temporary context declarations only; never production assignments or quantity attribution.
export interface ProductionDetailsInput {
  readonly order: string; readonly part: string; readonly operation: string;
  readonly localStart: string; readonly offset: string; readonly note: string;
}
export interface ProductionDetailsEntry extends ProductionDetailsInput {
  readonly id: string; readonly machineId: string; readonly author: string;
  readonly recordedAt: string; readonly effectiveAt: string;
}
export const emptyProductionDetails: ProductionDetailsInput = {
  order: "", part: "", operation: "", localStart: "", offset: "+05:30", note: "",
};
export function addProductionDetails(entries: readonly ProductionDetailsEntry[], input: ProductionDetailsInput,
  machineId: string, author: string, now: string, acknowledgeBackdated: boolean): { entries: readonly ProductionDetailsEntry[]; error?: string } {
  const fail = (error: string) => ({ entries, error });
  if (!machineId || !author) return fail("Select one sample machine and persona first.");
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(input.localStart)
    || !/^[+-](?:0\d|1[0-3]):[0-5]\d$|^[+-]14:00$/.test(input.offset))
    return fail("Provide a valid effective date, time and UTC offset.");
  // Validate the calendar independently: Date.parse otherwise normalizes invalid February dates.
  const local = Date.parse(`${input.localStart}:00Z`);
  if (!Number.isFinite(local) || new Date(local).toISOString().slice(0, 16) !== input.localStart)
    return fail("Provide a valid calendar date and time.");
  const effective = Date.parse(`${input.localStart}:00${input.offset}`);
  if (!Number.isFinite(effective)) return fail("Provide a valid effective date, time and UTC offset.");
  const previous = entries.filter(entry => entry.machineId === machineId);
  if (previous.some(entry => Date.parse(entry.effectiveAt) >= effective))
    return fail("Conflicting effective time. This preview only appends later job changes; equal or earlier entries need a separately designed correction workflow. History was not replaced.");
  if (effective < Date.parse(now) && !acknowledgeBackdated)
    return fail("Backdated declaration: explicitly acknowledge the declared past start before saving. It does not change historical production attribution.");
  const entry: ProductionDetailsEntry = { ...input, order: input.order.trim(), part: input.part.trim(),
    operation: input.operation.trim(), id: `production-details-${entries.length + 1}`,
    machineId, author, recordedAt: now, effectiveAt: new Date(effective).toISOString() };
  return { entries: [...entries, entry] };
}
export function productionDetailsAt(entries: readonly ProductionDetailsEntry[], machineId: string, at: string) {
  return entries.filter(entry => entry.machineId === machineId && Date.parse(entry.effectiveAt) <= Date.parse(at))
    .sort((a, b) => Date.parse(b.effectiveAt) - Date.parse(a.effectiveAt))[0];
}
