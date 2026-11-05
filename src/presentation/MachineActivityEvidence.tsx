import { useState } from 'react';
import { formatActivityDuration, type ActivityReport } from '../application/machine-activity-report.ts';
import { formatReportTimestamp } from './date-formatting.ts';
import './machine-activity.css';

// Shared evidence rendering: callers own scope, validation and request lifecycle.
export function MachineActivityEvidence({ report }: { readonly report: ActivityReport }) {
  const [pageSize, setPageSize] = useState(50);
  const [page, setPage] = useState(1);
  const rowCount = report.intervals.length;
  const pageCount = Math.max(1, Math.ceil(rowCount / pageSize));
  const visiblePage = Math.min(page, pageCount);
  const rowStart = (visiblePage - 1) * pageSize;
  const visibleIntervals = report.intervals.slice(rowStart, rowStart + pageSize);
  return <>
      <p>Selected interval: <time dateTime={report.fromInclusive}>{formatReportTimestamp(report.fromInclusive)}</time> – <time dateTime={report.toExclusive}>{formatReportTimestamp(report.toExclusive)}</time> (exclusive end).</p>
      <p>Read as of: <time dateTime={report.readAsOf}>{formatReportTimestamp(report.readAsOf)}</time>. State processor {report.stateProcessorId}; stream {report.streamKey}; checkpoint {report.throughPosition ?? 'No evaluation'}. Freshness is shown separately on the Machine status page.</p>
      <div className="activity-totals">{report.totals.map(total => <div key={total.state}><strong>{total.state}</strong><span>{formatActivityDuration(total.durationTicks)}</span></div>)}
        <div><strong>No stored interval</strong><span>{formatActivityDuration(report.unrepresentedTicks)}</span></div></div>
      <h3>Stored state timeline</h3>
      <ul className="activity-legend">{report.totals.map(t => <li key={t.state}><span className={`activity-swatch activity-${t.state}`} aria-hidden="true" />{t.state}</li>)}<li><span className="activity-swatch activity-unrepresented" aria-hidden="true" />No stored interval</li></ul>
      <p>Grey areas have no stored interval. Unknown is a recorded state. Select interval details below for exact timestamps and duration.</p>
      <svg viewBox="0 0 800 60" role="img" aria-label="Stored machine activity timeline; exact intervals in the following table">
        <rect x="0" y="10" width="800" height="35" fill="#e2e8f0" />
        {report.intervals.map(i => {
          const start = Date.parse(report.fromInclusive); const span = Date.parse(report.toExclusive) - start;
          return <rect key={i.position} x={(Date.parse(i.startedAtUtc) - start) / span * 800} y="10"
            width={(Date.parse(i.endedAtUtc) - Date.parse(i.startedAtUtc)) / span * 800} height="35" className={`activity-${i.state}`}>
            <title>{i.state}: {formatReportTimestamp(i.startedAtUtc)} – {formatReportTimestamp(i.endedAtUtc)}; {formatActivityDuration(i.durationTicks)}</title></rect>;
        })}
      </svg>
      {report.intervals.length === 0 && <p>No closed stored intervals returned for this selection.</p>}
      <nav className="activity-pagination" aria-label="Activity table pagination">
        <label>Rows per page<select value={pageSize} onChange={event => {
          const size = Number(event.currentTarget.value);
          if (size === 25 || size === 50 || size === 100) { setPageSize(size); setPage(1); }
        }}><option value={25}>25</option><option value={50}>50</option><option value={100}>100</option></select></label>
        <p role="status">Rows {rowCount === 0 ? 0 : rowStart + 1}–{Math.min(rowStart + pageSize, rowCount)} of {rowCount.toLocaleString('en-IN')} · Page {visiblePage} of {pageCount}</p>
        <button type="button" disabled={visiblePage === 1} aria-controls="activity-interval-table" onClick={() => setPage(visiblePage - 1)}>Previous</button>
        <button type="button" disabled={visiblePage === pageCount} aria-controls="activity-interval-table" onClick={() => setPage(visiblePage + 1)}>Next</button>
      </nav>
      <p className="activity-page-note">Only the interval table is paged. Totals and timeline cover the entire loaded range.</p>
      <div className="activity-table-scroll" role="region" aria-label="Exact stored activity intervals" tabIndex={0}>
        <table id="activity-interval-table"><caption>Exact stored activity intervals (displayed in IST)</caption><thead><tr><th>State</th><th>From</th><th>To</th><th>Duration</th><th>Source details</th></tr></thead>
          <tbody>{visibleIntervals.map(i => <tr key={i.position}><td>{i.state}</td>
            <td><time dateTime={i.startedAtUtc}>{formatReportTimestamp(i.startedAtUtc)}</time></td>
            <td><time dateTime={i.endedAtUtc}>{formatReportTimestamp(i.endedAtUtc)}</time></td>
            <td>{formatActivityDuration(i.durationTicks)}</td><td><details><summary>Position {i.position}</summary>
              <p>Instance {i.instanceId}; sequence {i.sequence}</p><p>Original UTC: {i.sourceStartedAtUtc} – {i.sourceEndedAtUtc}</p>
              <p>Selected UTC: {i.startedAtUtc} – {i.endedAtUtc}; exact duration {i.durationTicks} ticks (100 ns).</p></details></td></tr>)}</tbody></table>
      </div>
  </>;
}
