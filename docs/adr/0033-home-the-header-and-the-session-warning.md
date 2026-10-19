# ADR-0033: Home, the header, and the session warning across tabs

**Status:** Accepted
**Date:** 2026-10-19
**Decided by:** Product owner and architect

## Context

Until D4 the shell had no home: `/` redirected to the first page of the navigation. TF-170 asks for a header with search, notifications, help and profile, and for session expiry handling; TF-021 asks the interface to warn before the session ends and offer to continue, which WCAG 2.2.1 (timing adjustable) makes a rule for anyone who reads slowly or uses assistive technology. ADR-0024 says the home page is a moment where the art leads (brief section 2), and ADR-0025 item 8 already keeps the colour scheme out of a flash on reload. Notifications arrive with S4 and the organisation switcher with A8, so a header built to TF-170's letter today would carry two controls that do nothing.

## Decision

1. **The home page is the landing page after sign-in.** It sits at `/` for every signed-in principal, and "Home" is first in the navigation. It is the shell's own page, not a template: a moment, so the art leads and the work has no place on it.
2. **Its parts.**
   - The greeting by the time of day on this browser's clock and the person's display name, in the display family, beside the `Home` drawing, with the tenant mark small beside the tenant's name and large as a texture behind the drawing, never behind words.
   - One opener per module the principal may open, from the manifests filtered by `permissions_anywhere`, in the order of the navigation. A module declares `opener` in its manifest: its accent, one line on what it is for, an optional spot drawing in the brief's hand, and how many of its first entries to list (three by default). Its mark is the mandala generator seeded by the module's name, with the parameters the console's mark has had since A1 (`moduleMarkParameters`).
   - The quick actions the principal may perform. A module declares `quickActions` in its manifest: a label, a permission and a path. A candidate is one whose permission is held somewhere; the shell then asks `/me/access` at the principal's home unit (or the tenant-wide permissions, for a principal without one), and shows only what is allowed there. The action is a path: the page that owns it opens its dialog from the query (`/users?invite=1`, `/console/units?add=1`), so the home page, the search and a link all start it the same way. A section with nothing in it is not shown.
   - "Recently opened": the last five pages the person opened in this browser, per person, kept in `localStorage` under `iron.recent.<principal>` as a per-viewer convenience, filtered by what the principal may open now, a record named by its own name. With none, it says what will appear there.
3. **What may move.** The module marks and the tenant mark draw themselves in once per session (`iron.home.drawn` holds the session's id); reduced motion makes that a cut. Nothing else moves, the greeting is set when the page opens, and nothing on the page polls the API or changes while the person reads.
4. **No control without a function.** The header has the tenant's mark and name, search, help and the profile menu, and nothing that does not work yet. A header control that does nothing teaches people to ignore the header, so the notifications bell arrives with S4's notifications and the organisation switcher with A8's cross-organisation access, not before.
5. **Search** is `@mantine/spotlight` at the version of `@mantine/core`, 9.7.3, an exception to ADR-0003's two-week preference recorded in item 10. `Ctrl+K`, `Cmd+K` and a visible button open it; it lists every navigation entry the principal may open and Home, with the module as the description, and the quick actions under their own heading, so a page is found by its name or its module's. Enter opens the first match, Escape closes, and focus returns where it was. It reaches nothing the navigation and `/me/access` would not show.
6. **Help** is a drawer from the right edge with the shell's keys, the current page's keys and a line on where the guides will live. A route declares its keys in its manifest (`shortcuts`); the grid's, the tree's and the tabs' keys are shared lists beside the components.
7. **The colour scheme is kept per viewer.** The profile menu offers Light, Dark and "Same as the device", marks the current one with a tick and says it in words, and shows the person's home unit. Mantine's colour scheme manager keeps the choice under `iron.scheme`, the key `public/scheme.js` reads before the first paint, so a reload never flashes the wrong scheme; a shell test holds the two to one key, and the manager follows a choice made in another tab.
8. **The session warning** (TF-021, WCAG 2.2.1):
   - The timing is a pure module (`session-clock.ts`) on this browser's clock. `/me`'s `idle_expires_at` and `expires_at` are moved onto it with the offset of the server's `Date` header, so a wrong clock still warns on time.
   - Activity is any answer of the API but a 401: the server counted the request, so the idle end moves to the answer's time plus the idle span `/me` showed. The server writes its activity time at most once a minute, so the estimate can be up to a minute late; the warning's two minutes still leave the person more than one to answer.
   - The tabs of a browser share the session's times over a `BroadcastChannel` (`iron.session`): a later idle end wins, so activity in any tab extends every tab, and a warning open in one tab closes when another extends the session. A sign-out or an end in one tab takes every tab to the login page.
   - Two minutes before the idle end an alert dialog (`role="alertdialog"`, built from Mantine's parts because its Modal is a plain dialog) takes focus on "Stay signed in", announces its title and its message, and keeps focus until answered. Stay signed in is one request that touches the session, `GET /api/v1/me`.
   - Within two minutes of the absolute limit the dialog says that the session cannot be extended any further, and to save the work and sign in again; it offers to sign out or to go back to the work.
   - When the time runs out every tab forgets the session at once and the shell ends it on the server; the login page says why in one sentence (signed out after a while without activity, at the time limit, in another tab, or by the person) and returns to the same page after signing in.
9. **Two small reads for My profile.** `/me`'s session carries `started_at`, and `GET /api/v1/role-assignments/mine` answers the signed-in principal's own assignments without `authz.assignments.read`, because it shows nothing the principal does not hold. "Sign out everywhere" waits for the session console (TF-022); My profile offers Sign out, which ends the session in every tab of the browser.

10. **An exception to ADR-0003's two-week preference.** `@mantine/spotlight` 9.7.3 was published on 2026-10-17, two days before D4. The reason: its peer dependency on `@mantine/core` is pinned exactly (`"@mantine/core": "9.7.3"`), and core 9.7.3 is the platform version ADR-0003 decides, so no older Spotlight fits. Checked on 2026-10-19 before the merge, for core and Spotlight alike:
    - the same npm maintainer (`rtivital`) and the same publisher (GitHub Actions through npm's trusted publishing);
    - publish times 4.4 seconds apart (core at 12:30:10.994Z, Spotlight at 12:30:15.413Z);
    - SLSA v1 provenance on both, from `github.com/mantinedev/mantine`, branch `master`, workflow `.github/workflows/publish.yml`, commit `2d0a040f`, the same Actions run (39029359094, attempt 1);
    - `npm audit signatures` in `shell/` verifies the registry signatures of all 299 packages and the 127 attestations.

    A different publisher or provenance would have stopped the merge. The next Mantine upgrade moves core, hooks, form, notifications and Spotlight together, at a version two weeks old.

## Options considered

- A redirect to the first page (what S1 did): no moment, and a child-unit administrator lands on a page chosen by manifest order.
- The home page on `DashboardPage`: its cards are numbers, and the home page has none yet; a template for one page is not a template.
- A notifications placeholder and an organisation switcher now (TF-170's letter and the old outline): controls that do nothing.
- A search of our own on `Combobox`: more code for what Spotlight does, and a second palette to keep accessible.
- Polling `/me` to know the session's end: every poll is activity, so the session would never end; and a silent `/me` near the end would extend a session nobody used.
- Treating mouse movement and typing as activity: the server cannot see them, so the shell would promise a session the server has already ended.
- The session's times in `localStorage` with the `storage` event instead of a `BroadcastChannel`: storage writes on every answer, and an event model built for something else.
- Mantine's Modal for the warning: it cannot take `role="alertdialog"`.
- Mantine's default key `mantine-color-scheme-value`: it works, but the key would belong to a library version rather than to us, beside `iron.density`.

## Consequences

- A module gives the home page an opener, quick actions and help keys by declaring them in its manifest; the developer guide says how.
- The session warning has one source of time per tab and agrees across tabs without a request; the cost is an estimate up to a minute late after activity, bounded and documented.
- Every answer of the API reaches the session clock through `onResponse` in the client; a module that calls the API another way would not count as activity, which is one more reason for `request()`.
- Node's `BroadcastChannel` reaches every worker of a test run, so the shell's tests give each file channels of their own (`src/test/setup.ts`).
- Revisit when S4 brings notifications (the bell), A8 brings the organisation switcher, TF-022 brings "sign out everywhere", and when tenant settings make the idle and absolute limits configurable (the warning reads them from `/me` and needs no change).
