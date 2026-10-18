import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

// The shell is served by its own container behind the ingress; in development Vite proxies the API paths to
// `make run-api` so the session cookie stays first-party (ADR-0020).
const apiPaths = ["/api", "/auth", "/connect", "/health", "/.well-known"];
const apiTarget = process.env.IRON_API_URL ?? "http://localhost:5100";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: Object.fromEntries(apiPaths.map((path) => [path, { target: apiTarget, changeOrigin: false }])),
  },
  build: {
    sourcemap: true,
    target: "es2022",
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    css: false,
  },
});
