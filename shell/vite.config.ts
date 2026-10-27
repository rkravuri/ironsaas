import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import { availableParallelism, freemem } from "node:os";
import { fileURLToPath, URL } from "node:url";

// The shell is served by its own container behind the ingress; in development Vite proxies the API paths to
// `make run-api` so the session cookie stays first-party (ADR-0020).
const apiPaths = ["/api", "/auth", "/connect", "/health", "/.well-known"];
const apiTarget = process.env.IRON_API_URL ?? "http://localhost:5100";

// The page tests' workers, by the memory there is (ADR-0044). Each worker holds a jsdom and a React tree, about 360 MB at
// its peak. Beside the running stack, straight after make test, eight workers took the free memory under 150 MB, the
// kernel swapped, and page tests ran two to six times slower than alone (S3 T1's measurement); four workers with 5.6 GB
// available swapped nothing and ran as fast as alone. So one worker per 1.25 GiB of available memory (os.freemem() is
// MemAvailable on Linux), at least 2, and at most half the cores and 8, as before.
const workers = Math.max(
  2,
  Math.min(8, Math.floor(availableParallelism() / 2), Math.floor(freemem() / (1.25 * 2 ** 30))),
);

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: Object.fromEntries(apiPaths.map((path) => [path, { target: apiTarget, changeOrigin: false }])),
  },
  // Vite puts this placeholder in a meta tag and on its script and style tags; nginx replaces it with each response's
  // nonce, which the content security policy names for style elements (ADR-0053).
  html: { cspNonce: "CSP_NONCE" },
  build: {
    sourcemap: true,
    target: "es2022",
  },
  test: {
    exclude: ["e2e/**", "node_modules/**", "dist/**"],
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    css: false,
    maxWorkers: workers,
    // Each test file's results for make trace (ADR-0044), beside the console's.
    reporters: ["default", "junit"],
    outputFile: { junit: "../artifacts/test-results/shell/vitest.xml" },
  },
});
