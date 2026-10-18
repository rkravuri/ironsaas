import type { Page } from "@playwright/test";

/** Twenty users, as the Identity API lists them, so the real Users page runs against the preview without a stack. */
export const users = Array.from({ length: 20 }, (_, index) => {
  const n = index + 1;
  const names = ["Ada Lovelace", "Grace Hopper", "Linus Pauling", "Marie Curie", "Alan Turing"];
  return {
    id: `01a14e12-0000-7000-8000-${String(n).padStart(12, "0")}`,
    login_id: `user${String(n).padStart(2, "0")}@acme.localhost`,
    display_name: `${names[index % names.length] ?? "Someone"} ${n}`,
    type: n % 7 === 0 ? "service" : "user",
    status: n % 5 === 0 ? "invited" : "active",
    email: `user${n}@example.com`,
    last_login_at: n % 3 === 0 ? null : "2026-10-17T08:15:00Z",
    created_at: "2026-10-01T09:00:00Z",
    updated_at: "2026-10-02T09:00:00Z",
    version: 1,
  };
});

export const me = {
  id: "01a14e12-f3a8-7155-b5b6-17c3abceae77",
  login_id: "admin@acme.localhost",
  display_name: "Admin",
  type: "user",
  permissions: ["identity.users.read"],
  session: { id: "01a14e12-0000-7000-8000-000000000001", expires_at: "2026-10-19T08:00:00Z", idle_expires_at: "2026-10-19T07:00:00Z" },
};

function page(url: string) {
  const params = new URL(url).searchParams;
  const pageNumber = Math.max(1, Number(params.get("page_number") ?? "1"));
  const pageSize = Math.max(1, Number(params.get("page_size") ?? "50"));
  const start = (pageNumber - 1) * pageSize;
  const totalPages = Math.max(1, Math.ceil(users.length / pageSize));
  return {
    data: users.slice(start, start + pageSize),
    meta: { page_number: pageNumber, page_size: pageSize, total_count: users.length, total_pages: totalPages, has_previous: pageNumber > 1, has_next: pageNumber < totalPages, page_size_capped: false, max_page_size: 200 },
    links: { self: url, first: url, previous: null, next: null, last: url },
  };
}

/** A signed-in administrator who may read users, and the users list from memory; every other call answers 401. */
export async function stubSignedIn(target: Page): Promise<void> {
  // Playwright tries the routes newest first, so the catch-all goes in first and the specific answers after it.
  await target.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: JSON.stringify({ status: 401, error_code: "identity.unauthenticated" }) }));
  await target.route("**/api/v1/me", (route) => route.fulfill({ json: me }));
  await target.route("**/api/v1/users**", (route) => route.fulfill({ json: page(route.request().url()) }));
}
