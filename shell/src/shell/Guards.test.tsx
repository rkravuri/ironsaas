import { screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { useSession } from "@/auth/session-context";
import { manifests } from "@/modules";
import { navigationFor } from "@/modules/registry";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { RequirePermission } from "./Guards";

/** The navigation labels the session leaves visible, as the layout builds them. */
function Navigation() {
  const session = useSession();
  return (
    <p data-testid="navigation">
      {navigationFor(manifests, session.permissions, { strict: true })
        .flatMap((group) => group.entries.map((entry) => entry.path))
        .join(" ")}
    </p>
  );
}

describe("the guards and the navigation", () => {
  it("follow the permissions held anywhere: a reader of one unit's users sees the Users page (ADR-0031) [ADR-0020, ADR-0031]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me([], ["identity.users.read"])) });
    renderWithProviders(
      <>
        <Navigation />
        <RequirePermission permission="identity.users.read">
          <p>The users</p>
        </RequirePermission>
      </>,
    );

    expect(await screen.findByText("The users")).toBeInTheDocument();
    expect(screen.getByTestId("navigation").textContent).toBe("/users /me");
  });

  it("remove the Users page for a role without identity.users.read, and the page answers no access [ADR-0020]", async () => {
    stubFetch({ "GET /api/v1/me": () => json(me(["authz.units.read"], ["authz.units.read"])) });
    renderWithProviders(
      <>
        <Navigation />
        <RequirePermission permission="identity.users.read">
          <p>The users</p>
        </RequirePermission>
      </>,
    );

    // The navigation waits for /me; the guard says no access before and after it.
    await waitFor(() => expect(screen.getByTestId("navigation").textContent).toBe("/me /console/units"));
    expect(screen.getByText("No access")).toBeInTheDocument();
    expect(screen.queryByText("The users")).toBeNull();
  });
});
