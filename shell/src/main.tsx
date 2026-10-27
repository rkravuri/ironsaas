import "@mantine/core/styles.css";
import "@mantine/notifications/styles.css";
import "@mantine/spotlight/styles.css";
import "@fontsource-variable/source-sans-3";
import "@fontsource-variable/fraunces";
import "@/design/global.css";
import "@/i18n";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { MantineProvider } from "@mantine/core";
import { Notifications } from "@mantine/notifications";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { App } from "./App";
import { SessionProvider } from "@/auth/session";
import { applyDensity, readDensity } from "@/design/density";
import { shareStyleNonce, styleNonce } from "@/design/csp";
import { colorSchemeManager } from "@/design/scheme";
import { ErrorBoundary } from "@/shell/ErrorBoundary";
import { z } from "zod";
import { cssVariablesResolver, theme } from "./theme";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
  },
});

// The density is on the root before anything renders, so the first paint is already the viewer's.
applyDensity(readDensity());
// Every library that makes a style element gets the page's nonce before it makes one (ADR-0053).
shareStyleNonce();
// Zod compiles its parsers with new Function unless told not to, and first tries whether it may: the policy allows no
// eval, so that try is a violation on every page. Interpreted parsing is all the shell needs.
z.config({ jitless: true });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <MantineProvider
      theme={theme}
      defaultColorScheme="auto"
      colorSchemeManager={colorSchemeManager}
      cssVariablesResolver={cssVariablesResolver}
      getStyleNonce={styleNonce}
    >
      <Notifications position="top-right" />
      <ErrorBoundary>
        <QueryClientProvider client={queryClient}>
          <SessionProvider>
            <App />
          </SessionProvider>
        </QueryClientProvider>
      </ErrorBoundary>
    </MantineProvider>
  </StrictMode>,
);
