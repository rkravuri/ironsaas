import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { ApiError } from "@/api/client";
import { renderWithProviders, stubFetch } from "@/test/render";
import { ErrorView } from "./ErrorBoundary";

const drawing = (name: string) => document.querySelector(`svg[data-drawing='${name}']`);

describe("the error view", () => {
  it("draws the closed gate for a 403 and says no access", () => {
    stubFetch({});
    renderWithProviders(<ErrorView error={new ApiError(403, { title: "Forbidden", error_code: "authz.forbidden" }, undefined)} />);
    expect(drawing("forbidden")).toHaveAttribute("aria-hidden", "true");
    expect(screen.getByRole("heading", { name: "No access" })).toBeInTheDocument();
  });

  it("draws the signpost for a page that does not exist", () => {
    stubFetch({});
    renderWithProviders(<ErrorView error={{ status: 404, statusText: "Not Found", internal: false, data: null }} />);
    expect(drawing("lost")).not.toBeNull();
    expect(screen.getByRole("heading", { name: "Not found" })).toBeInTheDocument();
  });

  it("draws the broken clock for anything else, with the correlation id when there is one", () => {
    stubFetch({});
    renderWithProviders(<ErrorView error={new ApiError(500, { title: "The request failed.", correlation_id: "corr-500" }, undefined)} />);
    expect(drawing("broken")).not.toBeNull();
    expect(screen.getByText("corr-500")).toBeInTheDocument();
  });
});
