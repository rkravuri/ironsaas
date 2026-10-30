import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { AboutSuitePage } from "./AboutSuitePage";

const about = {
  suite: "procurement",
  name: "eProcurement Suite",
  description: "Source to pay for regulated industries.",
  version: "2026.10.30.7",
  installation: {
    installed_at: "2026-10-30T09:00:00Z",
    installed_by_job: "01a15100-0000-7000-8000-00000000000a",
    installed_version: "2026.10.30.7",
    seeded_at: "2026-10-31T09:00:00Z",
    seeded_by_job: "01a15100-0000-7000-8000-00000000000b",
    seeded_version: "2026.10.31.1",
  },
  plan: "procurement-essentials",
  grace_days: 30,
  stale: false,
  modules: [
    {
      code: "purchasing",
      module: "purchasing",
      description: null,
      status: "enabled",
      reason: "in_grace",
      starts_on: "2026-01-01",
      ends_on: "2026-06-30",
      grace_ends_on: "2026-07-30",
    },
  ],
  features: [],
};

function open(answer: () => Response) {
  stubFetch({
    "GET /api/v1/me": () => json(me(["purchasing.suite.read"])),
    "GET /api/v1/suites/procurement/about": answer,
  });
  renderWithProviders(<AboutSuitePage />, { route: "/procurement/about" });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the eProcurement Suite's About page (TF-244)", () => {
  it("shows the release, when and by which job the suite was installed and last updated, its plan, and its module with its status in words [TF-244 AC1, ADR-0059]", async () => {
    open(() => json(about));

    expect(await screen.findByRole("heading", { level: 2, name: "About the eProcurement Suite" })).toBeInTheDocument();
    const suite = screen.getByRole("table", { name: "The suite" });
    expect(within(suite).getByRole("row", { name: /Release/ })).toHaveTextContent("2026.10.30.7");
    expect(within(suite).getByRole("row", { name: /Installed/ })).toHaveTextContent(
      "By the platform's job 01a15100-0000-7000-8000-00000000000a",
    );
    expect(within(suite).getByRole("row", { name: /Last updated/ })).toHaveTextContent("release 2026.10.31.1");

    expect(screen.getByText("eProcurement Essentials")).toBeInTheDocument();
    expect(screen.getByText(/keeps working for 30 more days/)).toBeInTheDocument();

    const modules = screen.getByRole("table", { name: "The suite's modules" });
    const purchasing = within(modules).getByRole("row", { name: /Purchasing/ });
    expect(within(purchasing).getByText("Included")).toBeInTheDocument();
    expect(within(purchasing).getByText(/^Ended on .+; keeps working until .+/)).toBeInTheDocument();
    expect(screen.getByText("The suite has no features of its own yet")).toBeInTheDocument();
  });

  it("says when the installation is not recorded yet and the tenant holds no plan of the suite [TF-244 AC1, ADR-0059]", async () => {
    open(() => json({ ...about, version: "development", installation: null, plan: null }));

    const suite = await screen.findByRole("table", { name: "The suite" });
    expect(within(suite).getByRole("row", { name: /Release/ })).toHaveTextContent("A development build");
    expect(within(suite).getByRole("row", { name: /Installed/ })).toHaveTextContent(/Not recorded yet/);
    expect(screen.getByText(/Your organisation has no plan for the suite in force/)).toBeInTheDocument();
  });

  it("says what went wrong with the correlation id when the suite's module is switched off [TF-244 AC5, ADR-0009]", async () => {
    open(() =>
      problem(403, {
        title: "Forbidden",
        detail: "This is not part of your organisation's subscription.",
        error_code: "entitlements.module_disabled",
      }),
    );

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("The suite's details could not be loaded");
    expect(alert).toHaveTextContent("corr-123");
  });
});
