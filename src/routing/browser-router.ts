import { parseApplicationRoute, type ApplicationRoute } from "./application-route.ts";

export interface BrowserRoutingPort {
  readonly location: {
    readonly href: string;
    readonly origin: string;
    readonly pathname: string;
  };
  readonly history: {
    pushState(data: unknown, unused: string, url?: string | URL | null): void;
  };
  addEventListener(type: "popstate", listener: () => void): void;
  removeEventListener(type: "popstate", listener: () => void): void;
}

export interface BrowserRouter {
  current(): ApplicationRoute;
  navigate(href: string): ApplicationRoute;
  subscribe(listener: () => void): () => void;
}

export function createBrowserRouter(browser: BrowserRoutingPort): BrowserRouter {
  return {
    current: () => parseApplicationRoute(browser.location.pathname, new URL(browser.location.href).search),
    navigate: (href) => {
      const target = new URL(href, browser.location.href);
      if (target.origin !== browser.location.origin) {
        throw new Error("Application navigation must remain on the dashboard origin.");
      }

      const nextPath = target.pathname + target.search;
      if (nextPath !== new URL(browser.location.href).pathname + new URL(browser.location.href).search) {
        browser.history.pushState(null, "", nextPath);
      }

      return parseApplicationRoute(target.pathname, target.search);
    },
    subscribe: (listener) => {
      const onPopState = () => listener();
      browser.addEventListener("popstate", onPopState);
      return () => browser.removeEventListener("popstate", onPopState);
    },
  };
}
