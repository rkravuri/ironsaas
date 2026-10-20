import { render, screen } from "@testing-library/react";
import { MantineProvider } from "@mantine/core";
import type { INoRowsOverlayParams } from "ag-grid-community";
import { describe, expect, it } from "vitest";
import { NoRowsOverlay } from "./NoRowsOverlay";

const params = (filtered: boolean) =>
  ({ api: { isAnyFilterPresent: () => filtered } }) as unknown as INoRowsOverlayParams;

describe("the grid's empty state", () => {
  it("shows the empty box and says nothing yet when no filter is set [ADR-0024, ADR-0026]", () => {
    render(
      <MantineProvider>
        <NoRowsOverlay {...params(false)} />
      </MantineProvider>,
    );
    expect(screen.getByRole("status")).toHaveTextContent("Nothing to show.");
    expect(document.querySelector("svg[data-drawing='nothingYet']")).toHaveAttribute("aria-hidden", "true");
  });

  it("shows the magnifier and says nothing matches when a filter is set [ADR-0024]", () => {
    render(
      <MantineProvider>
        <NoRowsOverlay {...params(true)} />
      </MantineProvider>,
    );
    expect(screen.getByRole("status")).toHaveTextContent("Nothing matches the filter.");
    expect(document.querySelector("svg[data-drawing='nothingFound']")).not.toBeNull();
  });
});
