/** Display formatting only. Civil production dates are never timezone-converted. */
export function formatReportDate(value: string): string {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    return match ? `${match[3]}/${match[2]}/${match[1]}` : value;
}

/** India display timezone; original exact timestamps remain in time dateTime attributes. */
export function formatReportTimestamp(value: string | Date): string {
    const date = value instanceof Date ? value : new Date(value);
    if (!Number.isFinite(date.getTime())) return String(value);
    const parts = new Intl.DateTimeFormat('en-GB', {
        timeZone: 'Asia/Kolkata', day: '2-digit', month: '2-digit', year: 'numeric',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    }).formatToParts(date);
    const get = (kind: string) => parts.find(part => part.type === kind)?.value ?? '';
    return `${get('day')}/${get('month')}/${get('year')} ${get('hour')}:${get('minute')}:${get('second')} IST`;
}
