import type { ReactNode } from "react";
import { render } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import { SessionProvider } from "@/auth/session";

/** The providers a page needs, minus the real router: tests mount pages under a memory router. */
export function renderWithProviders(ui: ReactNode, { route = "/" }: { route?: string } = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
        </SessionProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
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

export const me = (permissions: string[] = []) => ({
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  type: "user",
  permissions,
  session: { id: "01a14e12-0000-7000-8000-000000000001", expires_at: "2026-10-19T08:00:00Z", idle_expires_at: "2026-10-18T09:00:00Z" },
});
