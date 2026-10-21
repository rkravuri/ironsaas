import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { json, me, renderWithProviders, stubFetch } from "@/test/render";
import { SubscriptionPage } from "./SubscriptionPage";

const subscription = {
  plan: "enterprise",
  revision: 4,
  confirmed_at: "2026-10-20T09:00:00Z",
  stale: false,
  grace_days: 30,
  modules: [
    {
      code: "sourcing",
      module: "sourcing",
      subscription_type: "per_person",
      status: "enabled",
      reason: "in_grace",
      starts_on: "2026-01-01",
      ends_on: "2026-10-15",
      grace_ends_on: "2026-11-14",
      seats: { limit: 50, assigned: 48, remaining: 2 },
    },
  ],
  features: [
    {
      code: "authorization.role_transfer",
      description: "Export the tenant's roles to a file and import roles from another tenant",
      module: "authz",
      licence: null,
      status: "enabled",
      reason: "licensed",
      starts_on: "2026-10-19",
      ends_on: null,
      grace_ends_on: null,
    },
    {
      code: "sourcing.auctions",
      description: "Auctions with pre-bidding",
      module: "sourcing",
      licence: "sourcing",
      status: "disabled",
      reason: "not_licensed",
      starts_on: null,
      ends_on: null,
      grace_ends_on: null,
    },
  ],
};

function open(body: unknown) {
  stubFetch({
    "GET /api/v1/me": () => json(me(["entitlements.subscription.read"])),
    "GET /api/v1/subscription": () => json(body),
  });
  renderWithProviders(<SubscriptionPage />, { route: "/console/subscription" });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the subscription page (TF-040, TF-041)", () => {
  it("shows the plan, each module and feature with its status in words, why, until when, and the seats [TF-041 AC3, TF-172 AC3, ADR-0034]", async () => {
    open(subscription);

    expect(await screen.findByRole("heading", { level: 2, name: "Subscription" })).toBeInTheDocument();
    expect(screen.getByText("Enterprise")).toBeInTheDocument();
    expect(screen.getByText(/keeps working for 30 more days/)).toBeInTheDocument();

    const modules = screen.getByRole("table", { name: "Licensed modules" });
    const sourcing = within(modules).getByRole("row", { name: /sourcing/ });
    expect(within(sourcing).getByText("Per person")).toBeInTheDocument();
    expect(within(sourcing).getByText("Included")).toBeInTheDocument();
    expect(within(sourcing).getByText(/^Ended on .+; keeps working until .+/)).toBeInTheDocument();
    expect(within(sourcing).getByText("48 of 50 used, 2 left")).toBeInTheDocument();

    const features = screen.getByRole("table", { name: "Features" });
    const transfer = within(features).getByRole("row", { name: /Export the tenant's roles/ });
    expect(within(transfer).getByText("Included")).toBeInTheDocument();
    expect(within(transfer).getByText("In your plan")).toBeInTheDocument();
    expect(within(transfer).getByText("No end date")).toBeInTheDocument();
    const auctions = within(features).getByRole("row", { name: /Auctions with pre-bidding/ });
    expect(within(auctions).getByText("Not available")).toBeInTheDocument();
    expect(within(auctions).getByText("Not in your plan")).toBeInTheDocument();

    // Read only for the tenant: plans are the operator's.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
    expect(screen.getByText("The platform operator sets the plan. To change it, ask them.")).toBeInTheDocument();
  });

  it("says when there are no licensed modules, and warns when the subscription cannot be confirmed [ADR-0034]", async () => {
    open({ ...subscription, modules: [], stale: true });

    expect(await screen.findByText("No licensed modules yet")).toBeInTheDocument();
    const alert = screen.getByRole("alert");
    expect(alert).toHaveTextContent("The subscription cannot be confirmed");
    expect(alert).toHaveTextContent("Tell the platform operator.");
  });
});
