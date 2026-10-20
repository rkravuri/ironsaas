import type { ReactNode } from "react";
import { render } from "@testing-library/react";
import type { UserEvent } from "@testing-library/user-event";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import { SessionProvider } from "@/auth/session";
import { colorSchemeManager } from "@/design/scheme";
import { cssVariablesResolver, theme } from "@/theme";

/** The providers a page needs, minus the real router: tests mount pages under a memory router. */
export function renderWithProviders(ui: ReactNode, { route = "/" }: { route?: string } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider theme={theme} colorSchemeManager={colorSchemeManager} cssVariablesResolver={cssVariablesResolver}>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/**
 * Puts text into a field as a paste does: one input event. Typing key by key renders the page again for every character,
 * which is what made the form tests slow, and then late, on a loaded machine. Keep user.type for a test about keys.
 */
export async function fill(user: UserEvent, field: HTMLElement, text: string): Promise<void> {
  await user.click(field);
  await user.paste(text);
}

/** A fetch that answers from a table of handlers, one per method and path. */
export type FetchHandler = (init: RequestInit | undefined) => Response | Promise<Response>;

export function stubFetch(handlers: Record<string, FetchHandler>) {
  const calls: Array<{ key: string; init: RequestInit | undefined }> = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    const key = `${init?.method ?? "GET"} ${url}`;
    calls.push({ key, init });
    const handler = handlers[key];
    if (!handler) {
      return new Response(JSON.stringify({ title: "Not Found", status: 404, error_code: "test.unhandled", correlation_id: "test-correlation" }), {
        status: 404,
        headers: { "Content-Type": "application/problem+json", "X-Correlation-Id": "test-correlation" },
      });
    }
    return handler(init);
  });
  vi.stubGlobal("fetch", fetchMock);
  return { fetchMock, calls };
}

export const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

export const problem = (status: number, body: Record<string, unknown>, correlationId = "corr-123") =>
  new Response(JSON.stringify({ status, correlation_id: correlationId, ...body }), {
    status,
    headers: { "Content-Type": "application/problem+json", "X-Correlation-Id": correlationId },
  });

/** A session as /me shows it: started a minute ago, idle in an hour, absolute in twelve, unless a test moves them. */
export const sessionTimes = ({ idleIn = 60 * 60_000, endsIn = 12 * 60 * 60_000 }: { idleIn?: number; endsIn?: number } = {}) => ({
  id: "01a14e12-0000-7000-8000-000000000001",
  started_at: new Date(Date.now() - 60_000).toISOString(),
  expires_at: new Date(Date.now() + endsIn).toISOString(),
  idle_expires_at: new Date(Date.now() + Math.min(idleIn, endsIn)).toISOString(),
});

/** A /me answer: tenant-wide permissions, and those held anywhere (the tenant-wide ones unless a test says otherwise). */
export const me = (permissions: string[] = [], anywhere: string[] = permissions) => ({
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  type: "user",
  permissions,
  permissions_anywhere: anywhere,
  session: sessionTimes(),
});
