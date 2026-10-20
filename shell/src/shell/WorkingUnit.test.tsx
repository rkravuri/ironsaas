import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useSession } from "@/auth/session-context";
import { json, me, problem, renderWithProviders, stubFetch, type FetchHandler } from "@/test/render";
import { placeChoices, useWorkingUnitChoice } from "./working-unit";
import { WorkingUnitControl, WorkingUnitFailure } from "./WorkingUnit";

const step = (id: string, name: string) => ({ id, name });
const acme = step("u-acme", "Acme");
const sales = step("u-sales", "Sales");
const north = step("u-north", "North");
const finance = step("u-finance", "Finance");
const choice = (path: { id: string; name: string }[]) => ({ ...path.at(-1)!, code: path.at(-1)!.name.toLowerCase(), depth: path.length - 1, path });

/** The choices of a person homed at North with a role at Sales and its units, and one at Finance alone. */
const choices = [choice([acme, sales]), choice([acme, sales, north]), choice([acme, finance])];

const meWith = ({
  units = choices,
  working = north,
  all = false,
  available = true,
  acting = false,
}: {
  units?: typeof choices;
  working?: { id: string; name: string };
  all?: boolean;
  available?: boolean;
  acting?: boolean;
}) => ({
  ...me(["identity.users.read"]),
  home_unit: { ...north, code: "north" },
  working_unit: { ...working, code: working.name.toLowerCase() },
  all_units: all,
  working_units: { units, all_units_available: available },
  acting_for: acting ? { id: "x", name: "Ada Lovelace", duties: [], until: "2026-10-27T00:00:00Z" } : null,
});

/** The header's control and its failure note as the layout places them, with what /me says the person works for. */
function Working() {
  const state = useWorkingUnitChoice();
  const { me: signedIn } = useSession();
  return (
    <>
      <WorkingUnitControl choice={state} />
      <WorkingUnitFailure choice={state} />
      <p data-testid="working">{signedIn ? (signedIn.all_units ? "all" : signedIn.working_unit?.name) : ""}</p>
    </>
  );
}

function renderControl(handlers: Record<string, FetchHandler>) {
  const stub = stubFetch(handlers);
  renderWithProviders(<Working />);
  return stub;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("the choice of the unit you work for (TF-222, ADR-0038)", () => {
  it("is not in the header with one unit to choose", async () => {
    renderControl({ "GET /api/v1/me": () => json(meWith({ units: [choice([acme, sales, north])], available: false })) });
    await waitFor(() => expect(screen.getByTestId("working")).toHaveTextContent("North"));
    expect(screen.queryByRole("button", { name: /Choose the unit you work for/ })).toBeNull();
  });

  it("is not there while the person acts for someone, whose rights in full the lists show", async () => {
    renderControl({ "GET /api/v1/me": () => json(meWith({ acting: true })) });
    await waitFor(() => expect(screen.getByTestId("working")).toHaveTextContent("North"));
    expect(screen.queryByRole("button", { name: /Choose the unit you work for/ })).toBeNull();
  });

  it("names the unit, lists the units on the tree with the current one chosen, and switches", async () => {
    const { calls } = renderControl({
      "GET /api/v1/me": () => json(meWith({})),
      "POST /api/v1/me/working-unit": () => json(meWith({ working: finance })),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Working for North. Choose the unit you work for" }));
    // jsdom lays nothing out, so Mantine may keep the dropdown hidden: the items are queried hidden or not.
    const items = await screen.findAllByRole("menuitem", { hidden: true });
    expect(items[0]).toHaveTextContent("SalesUnder Acme");
    expect(items[1]).toHaveTextContent("North, chosen");
    expect(items[1]).toHaveAttribute("data-current", "true");
    expect(items[2]).toHaveTextContent("FinanceUnder Acme");
    expect(items[3]).toHaveTextContent("All my units");

    await user.click(items[2]!);

    expect(await screen.findByRole("button", { name: "Working for Finance. Choose the unit you work for" })).toBeInTheDocument();
    expect(screen.getByTestId("working")).toHaveTextContent("Finance");
    const posted = calls.find((call) => call.key === "POST /api/v1/me/working-unit");
    expect(JSON.parse(posted?.init?.body as string)).toEqual({ unit_id: finance.id });
  });

  it("offers All my units only where a role reaches several units at once, and chooses it", async () => {
    const { calls } = renderControl({
      "GET /api/v1/me": () => json(meWith({})),
      "POST /api/v1/me/working-unit": () => json(meWith({ all: true })),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /Choose the unit you work for/ }));
    await user.click(await screen.findByRole("menuitem", { name: /All my units/, hidden: true }));

    expect(await screen.findByRole("button", { name: "Working for All my units. Choose the unit you work for" })).toBeInTheDocument();
    expect(screen.getByTestId("working")).toHaveTextContent("all");
    const posted = calls.find((call) => call.key === "POST /api/v1/me/working-unit");
    expect(JSON.parse(posted?.init?.body as string)).toEqual({ all_units: true });
  });

  it("has no All my units when no role reaches several units", async () => {
    renderControl({ "GET /api/v1/me": () => json(meWith({ available: false })) });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /Choose the unit you work for/ }));
    await screen.findAllByRole("menuitem", { hidden: true });
    expect(screen.queryByRole("menuitem", { name: /All my units/, hidden: true })).toBeNull();
  });

  it("says why a choice failed, with the correlation id, and keeps the unit", async () => {
    renderControl({
      "GET /api/v1/me": () => json(meWith({})),
      "POST /api/v1/me/working-unit": () =>
        problem(403, { error_code: "identity.working_unit.not_yours", detail: "You may not work for that unit. Choose your home unit or a unit where you hold a role." }, "corr-unit"),
    });
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: /Choose the unit you work for/ }));
    await user.click(await screen.findByRole("menuitem", { name: /Finance/, hidden: true }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("You may not work for that unit.");
    expect(alert).toHaveTextContent("corr-unit");
    await waitFor(() => expect(screen.getByTestId("working")).toHaveTextContent("North"));
  });
});

describe("the choices on the tree", () => {
  it("indents a unit under the choices above it, and writes the path above a unit with none", () => {
    const placed = placeChoices(choices);

    expect(placed.map((p) => [p.unit.name, p.indent, p.above])).toEqual([
      ["Sales", 0, ["Acme"]],
      ["North", 1, []],
      ["Finance", 0, ["Acme"]],
    ]);
  });
});
