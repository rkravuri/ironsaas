import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { renderWithProviders, stubFetch } from "@/test/render";
import { ConsolePage, DashboardPage, ListPage, RecordPage, WizardPage } from "./index";

describe("the list template", () => {
  it("opens with the title and one line, offers the density switch, and holds the grid [ADR-0027]", () => {
    stubFetch({});
    renderWithProviders(
      <ListPage
        title="Users"
        description="Everyone who can sign in."
        actions={<button type="button">New</button>}
        filters={<input aria-label="Search" />}
      >
        <div role="region" aria-label="The grid" />
      </ListPage>,
    );
    expect(screen.getByRole("heading", { level: 2, name: "Users" })).toBeInTheDocument();
    expect(screen.getByText("Everyone who can sign in.")).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "Density" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "New" })).toBeInTheDocument();
    expect(screen.getByLabelText("Search")).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "The grid" })).toBeInTheDocument();
  });
});

describe("the record template", () => {
  it("shows the header with its status in words and switches panels through tabs [TF-172 AC3, ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    renderWithProviders(
      <RecordPage
        header={{
          title: "Invoice 1042",
          code: "INV-1042",
          status: { label: "Approved", tone: "success" },
          meta: [{ label: "Owner", value: "Ada" }],
        }}
        tabs={[
          { value: "details", label: "Details", panel: <p>The details</p> },
          { value: "history", label: "History", panel: <p>The history</p> },
        ]}
      />,
    );
    expect(screen.getByRole("heading", { level: 2, name: "Invoice 1042" })).toBeInTheDocument();
    expect(screen.getByText("Approved")).toBeInTheDocument();
    expect(screen.getByText("INV-1042")).toBeInTheDocument();
    expect(screen.getByText("The details")).toBeInTheDocument();
    await user.click(screen.getByRole("tab", { name: "History" }));
    expect(await screen.findByText("The history")).toBeInTheDocument();
  });
});

function Wizard() {
  const [active, setActive] = useState(0);
  return (
    <WizardPage
      title="New unit"
      steps={[{ label: "Name" }, { label: "Parent" }, { label: "Confirm" }]}
      active={active}
      onBack={() => setActive((step) => step - 1)}
      onNext={() => setActive((step) => step + 1)}
      summary={<p>So far: nothing</p>}
    >
      <p>Step {active + 1}</p>
    </WizardPage>
  );
}

describe("the wizard template", () => {
  it("walks the steps with back and next, and says finish on the last one [ADR-0027]", async () => {
    stubFetch({});
    const user = userEvent.setup();
    renderWithProviders(<Wizard />);
    expect(screen.getByRole("button", { name: "Back" })).toBeDisabled();
    expect(screen.getByRole("complementary", { name: "What you have entered" })).toHaveTextContent("So far: nothing");
    await user.click(screen.getByRole("button", { name: "Next" }));
    await user.click(screen.getByRole("button", { name: "Next" }));
    expect(screen.getByText("Step 3")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Finish" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Back" }));
    expect(screen.getByText("Step 2")).toBeInTheDocument();
  });
});

describe("the console and dashboard templates", () => {
  it("list the console's sections in a contents nav and under their headings [ADR-0027]", () => {
    stubFetch({});
    renderWithProviders(
      <ConsolePage
        title="Settings"
        sections={[
          { id: "general", title: "General", description: "The tenant's name.", children: <p>Name field</p> },
          { id: "mail", title: "Mail", children: <p>Mail field</p> },
        ]}
      />,
    );
    const nav = screen.getByRole("navigation", { name: "On this page" });
    expect(within(nav).getByRole("link", { name: "General" })).toHaveAttribute("href", "#general");
    expect(screen.getByRole("heading", { level: 3, name: "General" })).toBeInTheDocument();
    expect(screen.getByText("Mail field")).toBeInTheDocument();
  });

  it("show a dashboard card per number in the display family [ADR-0027]", () => {
    stubFetch({});
    renderWithProviders(
      <DashboardPage
        title="Today"
        cards={[
          { id: "open", label: "Open orders", value: "128", hint: "12 since yesterday" },
          { id: "late", label: "Late", value: "3", tone: "danger" },
        ]}
      />,
    );
    const card = screen.getByRole("region", { name: "Open orders" });
    expect(within(card).getByText("128").style.fontFamily).toContain("iron-font-display");
    expect(screen.getByRole("region", { name: "Late" })).toHaveTextContent("3");
  });
});
