import { useEffect, useState, type FormEvent } from "react";

type Reason = { stopReasonId: string; reasonCode: string; displayName: string; category: string; isPlanned: boolean };
const categories = ["Breakdown", "Setup", "Tooling", "Material", "Quality", "Operator", "Planned", "Other"] as const;

export default function LiveStopReasonMaster() {
  const [reasons, setReasons] = useState<Reason[] | null>(null);
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [category, setCategory] = useState<string>("Breakdown");
  const [planned, setPlanned] = useState(false);
  const [retry, setRetry] = useState(0);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    setReasons(null);
    void fetch("/api/planning/v2/stop-reasons", { credentials: "include", signal: controller.signal })
      .then(async response => {
        if (!response.ok) throw new Error(`Reason Master unavailable (HTTP ${response.status})`);
        const data: { reasons: Reason[]; isSample: boolean } = await response.json();
        if (data.isSample || !Array.isArray(data.reasons)) throw new Error("Invalid live Reason Master response");
        setReasons(data.reasons);
      }).catch(e => { if (!controller.signal.aborted) setError(String(e)); });
    return () => controller.abort();
  }, [retry]);
  async function submit(event: FormEvent) {
    event.preventDefault(); setError(""); setNotice("");
    setSaving(true);
    try {
      const response = await fetch("/api/planning/v2/stop-reasons", {
        method: "POST", credentials: "include", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reasonCode: code.trim(), displayName: name.trim(), category, isPlanned: planned })
      });
      if (!response.ok) {
        const payload: { error?: string } = await response.json().catch(() => ({}));
        throw new Error(payload.error ?? `Reason save failed (HTTP ${response.status})`);
      }
      setCode(""); setName(""); setNotice("Reason saved."); setRetry(v => v + 1);
    } catch (e) { setError(String(e)); } finally { setSaving(false); }
  }
  return <>
    <p>Site-specific reasons from SQL. These are classifications, not machine stop evidence.</p>
    <form onSubmit={submit}>
      <fieldset disabled={saving}><legend>Create stop reason</legend>
        <label>Reason code <input required maxLength={40} value={code} onChange={e => setCode(e.target.value)} /></label>
        <label>Description <input required maxLength={150} value={name} onChange={e => setName(e.target.value)} /></label>
        <label>Category <select value={category} onChange={e => setCategory(e.target.value)}>
          {categories.map(value => <option key={value} value={value}>{value}</option>)}
        </select></label>
        <label><input type="checkbox" checked={planned} onChange={e => setPlanned(e.target.checked)} /> Planned stoppage</label>
        <button type="submit">{saving ? "Saving…" : "Save reason"}</button>
      </fieldset>
    </form>
    {notice && <p role="status">{notice}</p>}
    {error && <p role="alert">{error}. No sample fallback.</p>}
    {reasons === null ? <p>Loading configured reasons…</p> : reasons.length === 0
      ? <p>No active reasons configured for this site.</p>
      : <table><thead><tr><th>Code</th><th>Reason</th><th>Category</th><th>Type</th></tr></thead>
        <tbody>{reasons.map(reason => <tr key={reason.stopReasonId}>
          <td>{reason.reasonCode}</td><td>{reason.displayName}</td><td>{reason.category}</td>
          <td>{reason.isPlanned ? "Planned" : "Unplanned"}</td>
        </tr>)}</tbody></table>}
    <button type="button" onClick={() => { setError(""); setRetry(v => v + 1); }}>Refresh reasons</button>
  </>;
}
