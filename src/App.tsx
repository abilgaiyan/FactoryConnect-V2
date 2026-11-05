import { lazy, Suspense } from "react";
import { CommissioningPage } from "./presentation/CommissioningPage.tsx";
import { overviewReturn } from "./routing/overview-return.ts";
import { ManagementOverviewPage } from "./presentation/ManagementOverviewPage.tsx";
import { MachineCounterReportPage } from "./presentation/MachineCounterReportPage.tsx";
import { MachineActivityReportPage } from "./presentation/MachineActivityReportPage.tsx";
import { formatReportDate } from "./presentation/date-formatting.ts";
import { FleetStatusPage } from "./presentation/FleetStatusPage.tsx";
import { DateRangePage } from "./presentation/DateRangePage.tsx";
import {
  type FormEvent,
  type MouseEvent,
  type PropsWithChildren,
  useState,
} from "react";

import type { DashboardApplicationRuntime } from "./application/application-runtime.ts";
import { dailyReportPath } from "./application/daily-report-navigation.ts";
import { productionDayPath } from "./application/production-day-navigation.ts";
import { ProductionDayOverviewSurface } from "./application/ProductionDayOverviewSurface.ts";
import { isProductionDaySelection } from "./application/production-day-reporting.ts";
import {
  isShiftPerformanceProductionDaySelection,
  shiftPerformancePath,
} from "./application/shift-performance-navigation.ts";
import { useDailyReport } from "./application/use-daily-report.ts";
import { useProductionDayOverview } from "./application/use-production-day-overview.ts";
import { useShiftPerformanceOverview } from "./application/use-shift-performance-overview.ts";
import { DailyReportPage } from "./presentation/DailyReportPage.tsx";
import { ShiftPerformancePage } from "./presentation/ShiftPerformancePage.tsx";
import { MachineCurrentStatePage } from "./presentation/MachineCurrentStatePage.tsx";
import { useMachineCurrentState } from "./application/use-machine-current-state.ts";
import { routePath, type ApplicationRoute } from "./routing/application-route.ts";
import { shouldHandleApplicationNavigation } from "./routing/navigation-policy.ts";
import { useApplicationRouter } from "./routing/use-application-router.ts";

const SupervisorWorkspace = import.meta.env?.DEV
  ? lazy(() => import("./presentation/SupervisorWorkspace.tsx"))
  : null;

export interface AppProps {
  readonly runtime: DashboardApplicationRuntime;
}

export function App({ runtime }: AppProps) {
  const { route, navigate } = useApplicationRouter();
  const returnToOverview = overviewReturn(window.location.search, runtime.configuration.sources);

  return (
    <div className="dashboard-shell">
      <header>
        <a href="#main-content">Skip to content</a>
        <p>FactoryConnect</p>
        <div className="dashboard-site-heading" aria-label="Configured reporting groups and sites">
          {[...new Set(runtime.configuration.sources.map(source =>
            `${source.groupName ?? 'Group not configured'} · ${source.siteId}`,
          ))].map(label => <span key={label}>{label}</span>)}
        </div>
        <nav aria-label="Dashboard">
          {SupervisorWorkspace && <ApplicationLink href="/supervisor-preview" navigate={navigate} current={route.kind === "supervisorPreview"}>Supervisor desk preview</ApplicationLink>}
          <ApplicationLink href="/commissioning" navigate={navigate} current={route.kind === "commissioning"}>Commissioning</ApplicationLink>{" "}
          <ApplicationLink href="/overview" navigate={navigate} current={route.kind === "overview"}>Overview</ApplicationLink>{" "}
          <ApplicationLink href="/fleet" navigate={navigate} current={route.kind === "fleet"}>Fleet status</ApplicationLink>{" "}
          <ApplicationLink href="/reports/range" navigate={navigate} current={route.kind === "dateRange"}>Date-range report</ApplicationLink>{" "}
          <ApplicationLink href="/" navigate={navigate} current={route.kind === "productionDayOverview"}>
            Production days
          </ApplicationLink>
        </nav>
      </header>
      <main id="main-content">
        {returnToOverview && <ApplicationLink href={returnToOverview} navigate={navigate}>Back to selected overview</ApplicationLink>}
        <RouteView route={route} navigate={navigate} runtime={runtime} />
      </main>
    </div>
  );
}

interface ApplicationLinkProps extends PropsWithChildren {
  readonly href: string;
  readonly current?: boolean;
  readonly navigate: (href: string) => void;
}

function ApplicationLink({ href, current = false, navigate, children }: ApplicationLinkProps) {
  const handleClick = (event: MouseEvent<HTMLAnchorElement>) => {
    const anchor = event.currentTarget;
    if (!shouldHandleApplicationNavigation(event, {
      href: anchor.href,
      target: anchor.target,
      hasDownload: anchor.hasAttribute("download"),
    }, window.location.origin)) {
      return;
    }

    event.preventDefault();
    navigate(anchor.href);
  };

  return <a href={href} aria-current={current ? "page" : undefined} onClick={handleClick}>{children}</a>;
}

interface RouteViewProps {
  readonly route: ApplicationRoute;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function RouteView({ route, navigate, runtime }: RouteViewProps) {
  switch (route.kind) {
    case "supervisorPreview": return SupervisorWorkspace
      ? <Suspense fallback={<p role="status">Loading screen review…</p>}><SupervisorWorkspace runtime={runtime} /></Suspense>
      : <section><h1>Supervisor workspace not enabled</h1><p>The screen-review preview is available only in development builds. No production data-entry service is connected.</p></section>;
    case "commissioning": return <CommissioningPage />;
    case "overview": return <ManagementOverviewPage runtime={runtime} search={route.search} navigate={navigate} />;
    case "fleet": return <FleetStatusPage runtime={runtime} />;
    case "dateRange": return <DateRangePage key={window.location.search} runtime={runtime} />;
    case "productionDayOverview":
      return <ProductionDaySelection navigate={navigate} sourceCount={runtime.configuration.sources.length} />;
    case "productionDayDetail":
      return <ProductionDayDetail productionDay={route.productionDay} navigate={navigate} runtime={runtime} />;
    case "shiftPerformance":
      return <ShiftPerformanceSelection key={route.productionDay} productionDay={route.productionDay} navigate={navigate} runtime={runtime} />;
    case "machineDetail":
      return <MachineDetail key={route.machineId} machineId={route.machineId} navigate={navigate} runtime={runtime} />;
    case "machineActivity":
      return <MachineDetail key={`activity:${route.machineId}`} view="activity" machineId={route.machineId} navigate={navigate} runtime={runtime} />;
    case "machineCounter":
      return <MachineDetail key={`counter:${route.machineId}`} view="counter" machineId={route.machineId} navigate={navigate} runtime={runtime} />;
    case "dailyReport":
      return <DailyReportSelection key={route.productionDay} productionDay={route.productionDay} navigate={navigate} runtime={runtime} />;
    case "notFound":
      return <section aria-labelledby="route-title"><RouteContext current="Not found" navigate={navigate} /><h1 id="route-title">Page not found</h1><p>The dashboard has no route for this path.</p><code>{route.path}</code></section>;
  }
}

interface ProductionDaySelectionProps {
  readonly navigate: (href: string) => void;
  readonly sourceCount: number;
}

function ProductionDaySelection({ navigate, sourceCount }: ProductionDaySelectionProps) {
  const [productionDay, setProductionDay] = useState("");
  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (isProductionDaySelection(productionDay)) {
      navigate(productionDayPath(productionDay));
    }
  };

  return (
    <section aria-labelledby="route-title">
      <h1 id="route-title">Production days</h1>
      <p>{sourceCount} configured reporting source{sourceCount === 1 ? "" : "s"}.</p>
      <form onSubmit={handleSubmit}>
        <label htmlFor="production-day">Production day</label>
        <input id="production-day" name="production-day" type="date" required value={productionDay} onChange={(event) => setProductionDay(event.currentTarget.value)} />
        <button type="submit">Load reporting data</button>
      </form>
    </section>
  );
}

interface ProductionDayDetailProps {
  readonly productionDay: string;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function ProductionDayDetail({ productionDay, navigate, runtime }: ProductionDayDetailProps) {
  return (
    <section aria-labelledby="route-title">
      <RouteContext current="Production day" navigate={navigate} />
      <h1 id="route-title">Production day</h1>
      {isProductionDaySelection(productionDay)
        ? (
          <>
            <nav aria-label="Production-day views">
              <ApplicationLink href={shiftPerformancePath(productionDay)} navigate={navigate}>
                Shift performance
              </ApplicationLink>{" "}
              <ApplicationLink href={dailyReportPath(productionDay)} navigate={navigate}>
                Daily report
              </ApplicationLink>
            </nav>
            <ProductionDayOverviewVertical
              key={productionDay}
              productionDay={productionDay}
              runtime={runtime}
              onProductionDayChange={(nextProductionDay) => navigate(productionDayPath(nextProductionDay))}
            />
          </>
        )
        : <p role="alert">The selected production day is not a valid calendar date.</p>}
    </section>
  );
}

interface ShiftPerformanceSelectionProps {
  readonly productionDay: string;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function ShiftPerformanceSelection({ productionDay, navigate, runtime }: ShiftPerformanceSelectionProps) {
  const validSelection = isShiftPerformanceProductionDaySelection(productionDay);
  const [selectedProductionDay, setSelectedProductionDay] = useState(validSelection ? productionDay : "");

  const handleSubmit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (isShiftPerformanceProductionDaySelection(selectedProductionDay)) {
      navigate(shiftPerformancePath(selectedProductionDay));
    }
  };

  return (
    <section aria-labelledby="route-title">
      <RouteContext current="Shift performance" navigate={navigate} />
      <h1 id="route-title">Shift performance</h1>
      {validSelection
        ? (
          <>
            <p>Selected production day: <time dateTime={productionDay}>{formatReportDate(productionDay)}</time></p>
            <nav aria-label="Production-day views">
              <ApplicationLink href={dailyReportPath(productionDay)} navigate={navigate}>
                Daily report
              </ApplicationLink>
            </nav>
          </>
        )
        : <p role="alert">The selected production day is not a valid calendar date.</p>}
      <form onSubmit={handleSubmit}>
        <label htmlFor="shift-production-day">Production day</label>
        <input
          id="shift-production-day"
          name="shift-production-day"
          type="date"
          required
          value={selectedProductionDay}
          onChange={(event) => setSelectedProductionDay(event.currentTarget.value)}
        />
        <button type="submit">Select production day</button>
      </form>
      {validSelection
        ? <ShiftPerformanceVertical productionDay={productionDay} runtime={runtime} />
        : null}
    </section>
  );
}

interface MachineDetailProps {
  readonly view?: "status" | "activity" | "counter";
  readonly machineId: string;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function MachineDetail({ machineId, navigate, runtime, view = "status" }: MachineDetailProps) {
  const source = runtime.configuration.sources.find(
    candidate => candidate.machineId.toLowerCase() === machineId.toLowerCase(),
  );

  const overviewContext = new URLSearchParams(window.location.search).get("overview");
  const contextSearch = overviewContext !== null && overviewReturn(window.location.search, runtime.configuration.sources)
    ? `?${new URLSearchParams({ overview: overviewContext })}` : "";
  const title = view === "activity" ? "Machine activity history" : view === "counter" ? "Raw counter inspection" : "Machine";
  return (
    <section aria-labelledby="route-title">
      <RouteContext current={title} navigate={navigate} />
      <h1 id="route-title">{title}</h1>
      {source === undefined
        ? <p role="alert">This machine is not configured for the dashboard.</p>
        : <>
          <nav aria-label="Machine views">
            <ApplicationLink href={routePath({ kind: "machineDetail", machineId: source.machineId }) + contextSearch} current={view === "status"} navigate={navigate}>Machine status</ApplicationLink>{" "}
            <ApplicationLink href={routePath({ kind: "machineActivity", machineId: source.machineId }) + contextSearch} current={view === "activity"} navigate={navigate}>Activity history</ApplicationLink>{" "}
            <ApplicationLink href={routePath({ kind: "machineCounter", machineId: source.machineId }) + contextSearch} current={view === "counter"} navigate={navigate}>Counter inspection</ApplicationLink>
          </nav>
          {view === "status" ? <ConfiguredMachineDetail source={source} runtime={runtime} />
            : <>
              <p>{source.displayName} · {source.siteId} / {source.productionLineId}</p>
              {view === "activity"
                ? <MachineActivityReportPage machineId={source.machineId} timeoutMilliseconds={runtime.configuration.requestTimeoutMilliseconds} />
                : <MachineCounterReportPage machineId={source.machineId} timeoutMilliseconds={runtime.configuration.requestTimeoutMilliseconds} />}
            </>}
        </>}
    </section>
  );
}

function ConfiguredMachineDetail(props: {
  readonly source: DashboardApplicationRuntime["configuration"]["sources"][number];
  readonly runtime: DashboardApplicationRuntime;
}) {
  const currentState = useMachineCurrentState(props.source.machineId, props.runtime);
  return <MachineCurrentStatePage source={props.source} state={currentState.state} refresh={currentState.refresh} />;
}

interface DailyReportSelectionProps {
  readonly productionDay: string;
  readonly navigate: (href: string) => void;
  readonly runtime: DashboardApplicationRuntime;
}

function DailyReportSelection({ productionDay, navigate, runtime }: DailyReportSelectionProps) {
  const report = useDailyReport(productionDay, runtime);

  return (
    <section aria-labelledby="route-title">
      <RouteContext current="Daily report" navigate={navigate} />
      <h1 id="route-title">Daily report</h1>
      <nav aria-label="Production-day views" className="daily-report-controls">
        <ApplicationLink href={productionDayPath(productionDay)} navigate={navigate}>
          Production day
        </ApplicationLink>{" "}
        <ApplicationLink href={shiftPerformancePath(productionDay)} navigate={navigate}>
          Shift performance
        </ApplicationLink>
      </nav>
      <DailyReportPage
        productionDay={productionDay}
        state={report.state}
        refresh={report.refresh}
      />
    </section>
  );
}

interface ShiftPerformanceVerticalProps {
  readonly productionDay: string;
  readonly runtime: DashboardApplicationRuntime;
}

function ShiftPerformanceVertical({ productionDay, runtime }: ShiftPerformanceVerticalProps) {
  const overview = useShiftPerformanceOverview(productionDay, runtime);
  return <ShiftPerformancePage state={overview.state} refresh={overview.refresh} />;
}

interface ProductionDayOverviewVerticalProps {
  readonly productionDay: string;
  readonly runtime: DashboardApplicationRuntime;
  readonly onProductionDayChange: (day: string) => void;
}

function ProductionDayOverviewVertical({
  productionDay,
  runtime,
  onProductionDayChange,
}: ProductionDayOverviewVerticalProps) {
  const overview = useProductionDayOverview(productionDay, runtime);

  return (
    <ProductionDayOverviewSurface
      productionDay={productionDay}
      overview={overview}
      onProductionDayChange={onProductionDayChange}
    />
  );
}

interface RouteContextProps {
  readonly current: string;
  readonly navigate: (href: string) => void;
}

function RouteContext({ current, navigate }: RouteContextProps) {
  return <nav aria-label="Breadcrumb"><ApplicationLink href="/" navigate={navigate}>Production days</ApplicationLink><span aria-current="page">{current}</span></nav>;
}
