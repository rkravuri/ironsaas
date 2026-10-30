import { afterEach, describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { json, me, problem, renderWithProviders, stubFetch } from "@/test/render";
import { RecordTagsControl } from "./VisibilityTagsControl";

afterEach(() => {
  vi.unstubAllGlobals();
});

const path = "/api/v1/record-tags/sample_record/r1";
const tags = {
  record_type: "sample_record",
  record_id: "r1",
  tags: [
    { code: "APAC", name: "Asia Pacific", active: true },
    { code: "OLD", name: "Old programme", active: false },
  ],
  may_change: true,
  choices: [
    { code: "APAC", name: "Asia Pacific", active: true },
    { code: "EMEA", name: "Europe", active: true },
  ],
};

describe("a record's visibility tags", () => {
  it("says the tags in words, a retired one said so, and none in words for a record without tags [ADR-0057]", async () => {
    stubFetch({
      "GET /api/v1/me": () => json(me()),
      [`GET ${path}`]: () => json(tags),
      "GET /api/v1/record-tags/sample_record/r2": () =>
        json({ ...tags, record_id: "r2", tags: [], may_change: false, choices: [] }),
    });
    renderWithProviders(
      <>
        <RecordTagsControl recordType="sample_record" recordId="r1" />
        <RecordTagsControl recordType="sample_record" recordId="r2" />
      </>,
    );

    const list = await screen.findByRole("list", { name: "Visibility tags" });
    expect(within(list).getByText("Asia Pacific (APAC)")).toBeInTheDocument();
    expect(within(list).getByText("Old programme (OLD), retired")).toBeInTheDocument();
    expect(
      await screen.findByText("No visibility tags: everyone who may see records of its unit sees it."),
    ).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Change tags" })).toHaveLength(1);
  });

  it("offers the tags the reader may put, and shows the refusal that names a tag they do not hold [TF-241 AC5]", async () => {
    const { calls } = stubFetch({
      "GET /api/v1/me": () => json(me()),
      [`GET ${path}`]: () => json(tags),
      [`PUT ${path}`]: () =>
        problem(403, {
          title: "Forbidden",
          detail: "You do not hold the tag OLD, so you cannot take it off a record.",
          error_code: "data.visibility.tag_not_held",
        }),
    });
    renderWithProviders(<RecordTagsControl recordType="sample_record" recordId="r1" />);
    const user = userEvent.setup();

    await user.click(await screen.findByRole("button", { name: "Change tags" }));
    const dialog = await screen.findByRole("dialog", { name: "Change the record's visibility tags" });
    expect(within(dialog).getByRole("checkbox", { name: "Asia Pacific (APAC)" })).toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: "Europe (EMEA)" })).not.toBeChecked();
    await user.click(within(dialog).getByRole("checkbox", { name: "Old programme (OLD), retired" }));
    await user.click(within(dialog).getByRole("checkbox", { name: "Europe (EMEA)" }));
    await user.click(within(dialog).getByRole("button", { name: "Save the tags" }));

    expect(await within(dialog).findByText(/You do not hold the tag OLD/)).toBeInTheDocument();
    const sent = calls.find((call) => call.key === `PUT ${path}`);
    expect(JSON.parse(sent?.init?.body as string)).toEqual({ codes: ["APAC", "EMEA"] });
  });
});
