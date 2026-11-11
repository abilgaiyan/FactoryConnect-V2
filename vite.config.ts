import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { handleMockApiRequest } from "./src/mock-api.ts";

function mockApiPlugin(): Plugin {
  const handler = (req: any, res: any, next: any) => {
    if (!req.url) return next();
    if (
      req.url === "/dashboard/config" ||
      req.url.startsWith("/api/") ||
      req.url.startsWith("/health")
    ) {
      let body = "";
      req.on("data", (chunk: any) => {
        body += chunk;
      });
      req.on("end", () => {
        const result = handleMockApiRequest(req.url!, req.method || "GET", body);
        if (result) {
          res.statusCode = result.status;
          res.setHeader("Content-Type", result.contentType);
          res.end(result.body);
        } else {
          next();
        }
      });
      return;
    }
    next();
  };

  return {
    name: "mock-api-plugin",
    configureServer(server) {
      server.middlewares.use(handler);
    },
    configurePreviewServer(server) {
      server.middlewares.use(handler);
    },
  };
}

// Fixture mode is explicit. Real-backend mode never installs mock API middleware.
const fixturesEnabled = process.env.VITE_ENABLE_FIXTURES === "true";
const backendTarget = process.env.FC2_DEV_API_URL || "https://localhost:5081";
if (!fixturesEnabled) {
  const target = new URL(backendTarget);
  if (target.protocol !== "https:" || target.username || target.password ||
      target.search || target.hash || target.pathname !== "/" ||
      !["localhost", "127.0.0.1", "[::1]"].includes(target.hostname)) {
    throw new Error("FC2_DEV_API_URL must be a loopback HTTPS origin without credentials or path");
  }
}
const backendProxy = fixturesEnabled ? undefined : {
  "/dashboard/config": { target: backendTarget, changeOrigin: true },
  "/api": { target: backendTarget, changeOrigin: true },
  "/health": { target: backendTarget, changeOrigin: true },
};

export default defineConfig({
  plugins: [react(), ...(fixturesEnabled ? [mockApiPlugin()] : [])],
  server: {
    port: 3000,
    host: "0.0.0.0",
    strictPort: true,
    proxy: backendProxy,
  },
  preview: {
    port: 3000,
    host: "0.0.0.0",
    strictPort: true,
  },
  build: {
    target: "esnext",
    outDir: "dist",
    emptyOutDir: true,
  },
});
