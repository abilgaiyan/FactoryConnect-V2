import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

import { App } from "./App.tsx";
import { createDashboardApplicationRuntime } from "./application/application-runtime.ts";
import "./presentation/dashboard.css";
import "./presentation/daily-report.css";
import "./presentation/fleet-range.css";

const rootElement = document.getElementById("root");
if (rootElement === null) {
  throw new Error("Dashboard root element was not found.");
}

async function bootstrap() {
  if (import.meta.env.DEV && window.location.pathname === "/supervisor-preview") {
    const { default: SupervisorWorkspace } = await import("./presentation/SupervisorWorkspace.tsx");
    createRoot(rootElement!).render(
      <div className="dashboard-shell">
        <header>
          <p>FactoryConnect</p>
          <nav aria-label="Screen review">
            <a href="/">Return to dashboard</a>
          </nav>
        </header>
        <main id="main-content">
          <SupervisorWorkspace />
        </main>
      </div>,
    );
  } else {
    try {
      const runtime = await createDashboardApplicationRuntime(window.location.origin);
      createRoot(rootElement!).render(
        <StrictMode>
          <App runtime={runtime} />
        </StrictMode>,
      );
    } catch {
      if (import.meta.env.VITE_ENABLE_FIXTURES !== "true") {
        createRoot(rootElement!).render(<main><h1>FactoryConnect unavailable</h1><p>Dashboard authority could not be loaded. No machine observations are inferred.</p></main>);
        return;
      }
      // Demonstration fixtures require explicit opt-in and are never an error fallback in real mode.
      const { MOCK_DASHBOARD_CONFIG } = await import("./mock-api.ts");
      const runtime = await createDashboardApplicationRuntime(
        window.location.origin,
        async (input, init) => {
          const { handleMockApiRequest } = await import("./mock-api.ts");
          const urlStr = input.toString();
          const mockResult = handleMockApiRequest(
            urlStr,
            init?.method || "GET",
            (init?.body as string) || "",
          );
          if (mockResult) {
            return new Response(mockResult.body, {
              status: mockResult.status,
              headers: { "Content-Type": mockResult.contentType },
            });
          }
          return new Response(JSON.stringify(MOCK_DASHBOARD_CONFIG), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        },
      );
      createRoot(rootElement!).render(
        <StrictMode>
          <App runtime={runtime} />
        </StrictMode>,
      );
    }
  }
}

void bootstrap();
