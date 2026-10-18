# ADR-0020: The shell

Date: 2026-10-18
Status: Accepted

## Context

The S1 plan (3.10): one React build serves every tenant hostname; modules register manifests; the shell filters navigation and routes by the permissions from `/me`; a route without a permission throws in development and is hidden in production; the error boundary shows the correlation id. The confirmed stack is Vite, React 19.3, TypeScript, Mantine 9.7.3, TanStack Query, react-i18next and Zod on Node 26 (ADR-0003).

## Decision

1. **One build, no tenant inside it.** `shell/` builds to static files with nothing tenant-specific; the hostname the browser used is the tenant. Every unit runs the same `foundation/iron-shell` image at the unit's tag (ADR-0019), an nginx that serves the files, answers every path with `index.html` and sends the security headers of TF-070.
2. **Same origin through the ingress.** On the tenant's hostnames the ingress sends `/api`, `/auth`, `/connect`, `/health` and `/.well-known` to the unit's API and everything else to the shell. The session cookie stays first-party, there is no CORS, and `connect-src 'self'` holds. In development Vite proxies the same paths to `make run-api`.
3. **Manifests.** A module exports a manifest: routes with the permission each needs (`null` for any signed-in principal), and navigation entries. The shell builds the router and the navigation from the manifests and the permissions in `/me`; a route or entry without a declared permission throws in a development build and is hidden in production. The permission names are the API's, so the shell and the API filter the same way; the route guard checks again at render time, so a typed address gets the same answer as the API would give. Entitlements and feature flags join the filter in S2.
4. **One API client.** `request()` sends JSON with the cookie, validates answers with Zod schemas, turns a problem (ADR-0009) into an `ApiError` with the error code and the correlation id (from the body, else the `X-Correlation-Id` header), and tells the session provider about a 401 from any call. Every error view shows the correlation id.
5. **Session.** `/me` is the one source: the signed-in principal, its permissions and the session expiry, read with TanStack Query and shared through a provider. Login re-reads it; logout and a 401 forget it.
6. **Strings go through react-i18next** from the start, English only in S1, so locales (TF-170) are a catalogue away.
7. **The grid rule is in place before the grid** (T16): ESLint refuses `ag-grid-*` imports outside `src/components/data-grid`, so the Enterprise switch stays in one file.
8. **Update jobs.** A running unit takes a new template or image through a fleet job of kind Update (unit and verify steps only), which is also the rolling-upgrade primitive of S3.

## Consequences

- A product adds a module by adding a manifest; the shell needs no change.
- Permissions are checked three times (navigation, route, API) and only the API's answer is authoritative; the first two are for honesty in the interface.
- The shell image is tiny and tenant-neutral; a shell release is a tag change applied with an update job.
