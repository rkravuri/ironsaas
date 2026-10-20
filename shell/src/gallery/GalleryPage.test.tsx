import { screen } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { describe, expect, it } from "vitest";
import en from "@/i18n/en.json";
import { renderWithProviders, stubFetch } from "@/test/render";
import GalleryPage from "./GalleryPage";

describe("the gallery", () => {
  it("shows every section in the scheme and density the URL asks for, with every contrast pair passing", async () => {
    stubFetch({});
    renderWithProviders(
      <Routes>
        <Route path="/gallery" element={<GalleryPage />} />
      </Routes>,
      { route: "/gallery?scheme=dark&density=compact" },
    );

    for (const name of Object.values(en.gallery.sections)) {
      expect(screen.getByRole("heading", { name })).toBeInTheDocument();
    }
    expect(screen.getByText("acme.localhost")).toBeInTheDocument();
    expect(screen.getByText("bravo.localhost")).toBeInTheDocument();
    const marks = (hostname: string) =>
      [...document.querySelectorAll(`svg[data-mandala="${hostname}"] path`)].map((path) => path.getAttribute("d"));
    expect(marks("acme.localhost")).not.toEqual(marks("bravo.localhost"));
    expect(document.documentElement.getAttribute("data-mantine-color-scheme")).toBe("dark");
    expect(document.documentElement.dataset.density).toBe("compact");
    // The grid is a lazy chunk (AG Grid); on a loaded laptop it arrives after the query's default one second.
    expect(await screen.findByRole("region", { name: en.gallery.grid.label }, { timeout: 10_000 })).toBeInTheDocument();
    expect(screen.queryAllByText(en.gallery.colour.fail)).toHaveLength(0);
    expect(screen.getAllByText(en.gallery.colour.pass).length).toBeGreaterThan(50);
    // The whole gallery renders here, templates and grid included; under load it needs more than the default 5 s.
  }, 20_000);
});
