import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { richTextSchema } from "@/api/rich-text";
import { RichTextView } from "./RichTextView";

describe("RichTextView", () => {
  it("renders the API's cleaned rich text as formatted text, under its label [TF-071 AC5, ADR-0053]", () => {
    const html = richTextSchema.parse("<p>Pay within <strong>30 days</strong>.</p><ul><li>One</li></ul>");

    render(<RichTextView html={html} label="Terms" />);

    const region = screen.getByLabelText("Terms");
    expect(region.querySelector("strong")?.textContent).toBe("30 days");
    expect(region.querySelectorAll("li")).toHaveLength(1);
  });
});
