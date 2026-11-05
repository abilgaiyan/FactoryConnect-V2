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

export default defineConfig({
  plugins: [react(), mockApiPlugin()],
  server: {
    port: 3000,
    host: "0.0.0.0",
    strictPort: true,
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
