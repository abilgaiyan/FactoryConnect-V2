import { useEffect, useState } from "react";
import LiveMachineShiftTargets from "./LiveMachineShiftTargets.tsx";
import LiveMachineShiftProduction from "./LiveMachineShiftProduction.tsx";
import LiveMachineTargetActual from "./LiveMachineTargetActual.tsx";
import LiveMachineProductionTrend from "./LiveMachineProductionTrend.tsx";
import LiveMachineActivityCoverage from "./LiveMachineActivityCoverage.tsx";
interface Machine { machineId: string; displayName: string }
type WorkspaceSection = "production" | "targets" | "comparison" | "trend" | "activity";
const sections: { id: WorkspaceSection; label: string }[] = [
 { id: "production", label: "Production entry" }, { id: "targets", label: "Shift targets" },
 { id: "comparison", label: "Target vs actual" }, { id: "trend", label: "Seven-day trend" },
 { id: "activity", label: "Machine activity" }
];
export default function SupervisorTargetEntry() {
 const [section,setSection]=useState<WorkspaceSection>("production"),[open,setOpen]=useState(false),[machines,setMachines]=useState<Machine[]|null>(null),[machineId,setMachineId]=useState(""),[businessDate,setBusinessDate]=useState(""),[error,setError]=useState(""),[retry,setRetry]=useState(0);
 useEffect(()=>{if(!open)return;const controller=new AbortController();setMachines(null);setError("");
 void fetch("/api/planning/v2/machines",{credentials:"include",signal:controller.signal}).then(async response=>{
 if(!response.ok)throw Error(`Machine list unavailable (HTTP ${response.status})`);
 const data:{machines:Machine[];isSample:boolean}=await response.json();
 if(data.isSample||!Array.isArray(data.machines))throw Error("Invalid machine list");
 setMachines(data.machines);
 }).catch(e=>{if(!controller.signal.aborted)setError(String(e));});
 return ()=>controller.abort();},[open,retry]);
 return <section aria-label="Supervisor production targets"><h2>Supervisor production</h2>
 <p>Choose a machine and production date, then open the section you need.</p>
 <button type="button" onClick={()=>setOpen(v=>!v)}>{open?"Close supervisor production":"Open supervisor production"}</button>
 {open&&<>{error&&<p role="alert">{error}. No sample fallback. <button type="button" onClick={()=>setRetry(v=>v+1)}>Retry machines</button></p>}
 {machines===null&&!error&&<p role="status">Loading registered machines…</p>}
 {machines?.length===0&&<p>No machines are registered for this site. Historical records do not automatically add machines to the machine list.</p>}
 {machines&&machines.length>0&&<><label>Machine <select value={machineId} onChange={e=>setMachineId(e.target.value)}><option value="">Select a registered machine</option>{machines.map(m=><option key={m.machineId} value={m.machineId}>{m.displayName}</option>)}</select></label>
 <label>Business date <input type="date" value={businessDate} onChange={e=>setBusinessDate(e.target.value)}/></label>
 {machineId&&/^\d{4}-\d{2}-\d{2}$/.test(businessDate)&&<>
 <nav aria-label="Supervisor production sections" style={{display:"flex",gap:8,flexWrap:"wrap",margin:"16px 0"}}>
 {sections.map(item=><button key={item.id} type="button" aria-pressed={section===item.id} onClick={()=>setSection(item.id)} style={{fontWeight:section===item.id?700:400}}>{item.label}</button>)}
 </nav>
 {section==="production"&&<LiveMachineShiftProduction key={"production:"+machineId+":"+businessDate} machineId={machineId} businessDate={businessDate}/>}
 {section==="targets"&&<LiveMachineShiftTargets key={machineId+":"+businessDate} machineId={machineId} businessDate={businessDate}/>}
 {section==="comparison"&&<LiveMachineTargetActual key={"comparison:"+machineId+":"+businessDate} machineId={machineId} businessDate={businessDate}/>}
 {section==="trend"&&<LiveMachineProductionTrend key={"trend:"+machineId+":"+businessDate} machineId={machineId} businessDate={businessDate}/>}
 {section==="activity"&&<LiveMachineActivityCoverage key={"activity:"+machineId+":"+businessDate} machineId={machineId} date={businessDate}/>}
 </></>}</>}
 </>}</section>;
}