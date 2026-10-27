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
import { styleNonce } from "@/design/csp";
import { colorSchemeManager } from "@/design/scheme";
import { ErrorBoundary } from "@/shell/ErrorBoundary";
import { cssVariablesResolver, theme } from "./theme";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
  },
});

// The density is on the root before anything renders, so the first paint is already the viewer's.
applyDensity(readDensity());

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
