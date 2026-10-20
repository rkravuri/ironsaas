import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { Alert, MantineProvider, Title } from "@mantine/core";
import { cssVariablesResolver, theme } from "./theme";

const mount = (ui: React.ReactNode) =>
  render(
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver}>
      {ui}
    </MantineProvider>,
  );

describe("the theme", () => {
  it("puts the display family on a display title of order 1 or 2 and nowhere else [ADR-0025]", () => {
    mount(
      <>
        <Title order={2} variant="display">
          Moment
        </Title>
        <Title order={3} variant="display">
          Small
        </Title>
        <Title order={2}>Work</Title>
      </>,
    );
    expect(screen.getByRole("heading", { name: "Moment" }).style.fontFamily).toContain("Fraunces");
    expect(screen.getByRole("heading", { name: "Small" }).style.fontFamily).not.toContain("Fraunces");
    expect(screen.getByRole("heading", { name: "Work" }).style.fontFamily).not.toContain("Fraunces");
  });

  it("knows the colours by their meaning [ADR-0025]", () => {
    mount(
      <Alert color="danger" title="Failed">
        The record was not saved.
      </Alert>,
    );
    const alert = screen.getByRole("alert");
    expect(alert.style.getPropertyValue("--alert-bg")).toContain("danger");
  });
});
