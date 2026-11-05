import { useCallback, useEffect, useMemo, useState } from "react";

import { createBrowserRouter, type BrowserRoutingPort } from "./browser-router.ts";
import type { ApplicationRoute } from "./application-route.ts";

export interface ApplicationRouterState {
  readonly route: ApplicationRoute;
  navigate(href: string): void;
}

export function useApplicationRouter(): ApplicationRouterState {
  const router = useMemo(
    () =>
      createBrowserRouter({
        location: window.location,
        history: window.history,
        addEventListener: (type, listener) => window.addEventListener(type, listener),
        removeEventListener: (type, listener) => window.removeEventListener(type, listener),
      } satisfies BrowserRoutingPort),
    [],
  );

  const [route, setRoute] = useState<ApplicationRoute>(() => router.current());

  useEffect(() => router.subscribe(() => setRoute(router.current())), [router]);

  const navigate = useCallback(
    (href: string) => {
      setRoute(router.navigate(href));
    },
    [router],
  );

  return { route, navigate };
}
